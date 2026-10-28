#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// ============================================================================
// NQDomOrderFlowProBiasFilteredV4 - NinjaTrader 8 strategy
//
// V4 CHANGES (logic fixes, from the V3 review)
//  1) Per-gate counters in the diagnostics (GATES line): how many evaluations were removed by the
//     range filter, the 5m trend gate, the no-chase gate and the opposite-flip gate.
//  2) Persistence: BiasFlipConfirm now only applies to OPPOSITE flips (BUY<->SELL). NONE->BUY is covered
//     by ConfirmSeconds alone (V3 stacked both = 3.5 s). New ConfirmGraceSeconds: a short dropout of the
//     signal no longer resets the persistence timer.
//  3) No-chase: measured from the mid price N seconds ago, in the TRADE direction only
//     (V3 used the chart bar open in both directions, which depended on the clock inside the bar).
//  4) 5-minute trend gate applies to CONTINUATION entries only (it killed every absorption reversal)
//     and uses completed 5m bars (the forming bar flickered around the EMA).
//  5) Entry style by signal type: continuation = marketable limit (best ask/bid +/- slip, short timeout);
//     reversal = passive limit at the defended level. Cooldown now starts at the FILL, and an unfilled
//     cancelled order re-arms the signal after a short retry delay (V3 locked the signal for 30 s +
//     neutral reset even when nothing was filled).
// Live Level-II / time-and-sales framework.
//
// ENTRY MODEL (DIRECTIONAL BIAS BUILD)
//  1. Direction bias is computed independently from weighted DOM-change OFI,
//     aggressive tape delta, recent price response, and static book imbalance.
//  2. Continuation requires a 3-of-4 directional vote; absorption reversal needs
//     OFI confirmation, contained price movement, and a supporting DOM clue.
//  3. Suspected-spoof filters veto entries toward the suspect side.
//  4. First signal is allowed after warmup; later entries require a neutral reset,
//     one entry per signal episode, and a short cooldown.
//
// EXIT MODEL
//  Fixed currency bracket only: default target $300 / stop $500 for the whole
//  strategy position. No breakeven, no trailing stop, no time exit, and no
//  discretionary DOM exits. Stop and target are an OCO bracket.
//
// LIMITATIONS
//  Public aggregated DOM cannot prove a participant's intent or identify a
//  native iceberg with certainty. Spoof/iceberg flags are probabilistic heuristics.
//  Full Level-II is live-only in NinjaTrader; this is not a Strategy Analyzer
//  historical backtest strategy. Compile and test in Sim/Playback first.
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NQDomOrderFlowProBiasFilteredV4 : Strategy
    {
        #region Internal types
        private sealed class BookLevel
        {
            public double Price;
            public long Size;
            public long PeakSize;
            public long TotalTraded;
            public long TradedSinceChange;
            public DateTime FirstSeen;
            public DateTime LastChange;
            public int LastPosition;
        }

        private sealed class TapeTrade
        {
            public DateTime Time;
            public double Price;
            public long Size;
            // +1 = buyer-initiated, -1 = seller-initiated, 0 = unclassified
            public int Aggressor;
        }

        private sealed class FlowDelta
        {
            public DateTime Time;
            // Positive means bid-side liquidity added / ask-side liquidity removed.
            // Negative means ask-side liquidity added / bid-side liquidity removed.
            public double SignedWeightedDelta;
        }

        private sealed class FlowMark
        {
            public DateTime Time;
            public double Price;
            public long Size;
        }

        private sealed class IcebergTrack
        {
            public DateTime FirstSeen;
            public DateTime LastActivity;
            public long Executed;
            public long PeakVisible;
            public long LastVisible;
            public int Replenishments;
            public bool WasReduced;
        }

        private sealed class FlowSnapshot
        {
            public bool Valid;
            public string InvalidReason = string.Empty;
            public double BestBid;
            public double BestAsk;
            public double SpreadTicks;
            public double Imbalance;
            public double OrderFlowImbalance;
            public double NetAggressorDelta;
            public double PriceMoveTicks;
            public int ClassifiedTrades;
            public int LongBiasScore;
            public int ShortBiasScore;
            public bool BuyBias;
            public bool SellBias;
            public string BiasReason = "NO_TRADE";
            public bool BullAbsorption;
            public bool BearAbsorption;
            public bool BidStacking;
            public bool AskStacking;
            public bool BidPulling;
            public bool AskPulling;
            public bool BidIceberg;
            public bool AskIceberg;
            public bool SuspectBidSpoof;
            public bool SuspectAskSpoof;
            public int LongConfirmations;
            public int ShortConfirmations;
        }

        private sealed class MidSample
        {
            public DateTime Time;
            public double Price;
        }
        #endregion

        #region State
        private readonly object bookLock = new object();
        private readonly object signalLock = new object();
        private readonly List<BookLevel> bids = new List<BookLevel>();
        private readonly List<BookLevel> asks = new List<BookLevel>();
        private readonly List<TapeTrade> tape = new List<TapeTrade>();
        private readonly List<FlowDelta> ofiEvents = new List<FlowDelta>();
        private readonly List<FlowMark> bidStackMarks = new List<FlowMark>();
        private readonly List<FlowMark> askStackMarks = new List<FlowMark>();
        private readonly List<FlowMark> bidPullMarks = new List<FlowMark>();
        private readonly List<FlowMark> askPullMarks = new List<FlowMark>();
        private readonly Dictionary<double, IcebergTrack> bidIcebergs = new Dictionary<double, IcebergTrack>();
        private readonly Dictionary<double, IcebergTrack> askIcebergs = new Dictionary<double, IcebergTrack>();

        private double lastBid;
        private double lastAsk;
        private double lastTradePrice;
        private int lastTradeDirection;
        private DateTime suspectBidSpoofUntil = DateTime.MinValue;
        private DateTime suspectAskSpoofUntil = DateTime.MinValue;
        private DateTime realtimeStarted = DateTime.MinValue;
        private DateTime lastEntryAt = DateTime.MinValue;
        private DateTime neutralSince = DateTime.MinValue;
        private DateTime longCandidateSince = DateTime.MinValue;
        private DateTime shortCandidateSince = DateTime.MinValue;
        private DateTime lastDiagnosticsAt = DateTime.MinValue;
        private DateTime tradeDate = DateTime.MinValue;

        private bool entryPending;
        private Order pendingEntryOrder;
        private DateTime pendingEntrySubmittedAt = DateTime.MinValue;
        private bool entryCancelRequested;
        private bool signalArmed;
        private bool hadPosition;
        private string activeEntrySignal = string.Empty;
        private int tradesToday;
        private bool rangeFilterBlocked;
        private double recentRangeTicks;
        private DateTime lastBiasDirectionChange = DateTime.MinValue;
        private string lastRawDirection = "NONE";
        private NinjaTrader.NinjaScript.Indicators.EMA trendEma5m;
        private double signalReferencePrice;

        // V4
        private string lastNonNoneDirection = string.Empty;
        private DateTime lastNonNoneAt = DateTime.MinValue;
        private DateTime lastLongRawAt = DateTime.MinValue;
        private DateTime lastShortRawAt = DateTime.MinValue;
        private DateTime lastUnfilledCancelAt = DateTime.MinValue;
        private double pendingTimeoutSeconds = 5.0;
        private bool pendingIsReversal;
        private readonly List<MidSample> midHistory = new List<MidSample>();
        private DateTime lastMidSampleAt = DateTime.MinValue;
        private long cntEval, cntCandidate, cntBlkRange, cntBlkHtf, cntBlkChase, cntBlkFlip, cntEntries, cntUnfilled;
        #endregion

        #region Defaults and lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "NQDomOrderFlowProBiasFilteredV4";
                Description = "V4: DOM strategy with per-gate diagnostics, single persistence, rolling no-chase, continuation-only 5m gate, marketable/passive entries by signal type. Sim testing only.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false; // bracket exits only
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 1;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.ByStrategyPosition;
                BarsRequiredToTrade = 1;
                IsInstantiatedOnEachOptimizationIteration = false;

                Contracts = 1;
                TargetDollars = 300;
                StopDollars = 500;

                ImbalanceLevels = 5;
                ImbalanceThreshold = 0.15;
                MaxSpreadTicks = 6;

                // Directional bias: all three evidence families are measured on a rolling window.
                // Continuation bias uses a 3-of-4 vote (OFI, tape delta, price response, static book imbalance).
                DirectionWindowSeconds = 3.0;
                OfiDepthLevels = 5;
                OfiThreshold = 15.0;
                DeltaThreshold = 25.0;
                MinPriceResponseTicks = 1;
                MaxAbsorptionCounterMoveTicks = 1;

                AbsorptionWindowSeconds = 3.0;
                AbsorptionMinVolume = 25;
                AbsorptionDominance = 1.25;
                AbsorptionMaxMoveTicks = 3;

                StackingWindowSeconds = 2.0;
                StackingDepthLevels = 5;
                StackingMinAdd = 5;
                StackingOrderMinSize = 12;
                MinStackingLevels = 1;

                PullWindowSeconds = 1.5;
                PullDepthLevels = 5;
                PullMinContracts = 8;
                PullVolumeThreshold = 25;
                MinPullingLevels = 1;

                IcebergWindowSeconds = 5.0;
                IcebergMinExecuted = 25;
                IcebergMultiple = 1.25;
                IcebergMinReplenishments = 1;

                LargeOrderMinSize = 35;
                SpoofLifetimeSeconds = 2.0;
                SpoofCancelFraction = 0.70;
                SpoofMaxTradedFraction = 0.15;
                SpoofCooldownSeconds = 2.0;

                ConfirmSeconds = 1.5;
                NeutralResetSeconds = 2.0;
                EntryCooldownSeconds = 30.0;
                WarmupSeconds = 10.0;
                MaxTradesPerDay = 10;
                EnableRangeFilter = true;
                RangeLookbackBars = 12;
                MinimumRangeTicks = 18;
                BiasFlipConfirmSeconds = 2.0;
                DiagnosticsSeconds = 5.0;
                EnableDiagnostics = true;
                EnableHigherTimeframeTrendFilter = true;
                TrendEmaPeriod = 20;
                MaxChaseTicks = 12;
                LimitOrderTimeoutSeconds = 5.0;
                LimitEntryOffsetTicks = 0;
                ChaseLookbackSeconds = 15.0;
                ConfirmGraceSeconds = 0.5;
                ApplyHtfGateToReversal = false;
                HtfUseCompletedBars = true;
                ContinuationSlipTicks = 1;
                ContinuationTimeoutSeconds = 2.0;
                RetryCooldownSeconds = 3.0;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);
                // Static currency offsets. NinjaTrader submits protective orders
                // after entry executions and OCO-links the stop and target.
                SetStopLoss("OFLongV4", CalculationMode.Currency, StopDollars, false);
                SetProfitTarget("OFLongV4", CalculationMode.Currency, TargetDollars);
                SetStopLoss("OFShortV4", CalculationMode.Currency, StopDollars, false);
                SetProfitTarget("OFShortV4", CalculationMode.Currency, TargetDollars);
            }
            else if (State == State.DataLoaded)
            {
                trendEma5m = EMA(Closes[1], TrendEmaPeriod);
            }
            else if (State == State.Realtime)
            {
                realtimeStarted = DateTime.UtcNow;
                // Arm the FIRST signal after warmup. Requiring a neutral period
                // before the very first signal can deadlock if the DOM is already
                // directional when the strategy is enabled.
                signalArmed = true;
                neutralSince = DateTime.MinValue;
                Print("[NQDomOrderFlowProBiasFilteredV4] REALTIME started. First qualified signal is allowed after DOM warmup.");
                Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] Fixed bracket: target ${0:0.00}, stop ${1:0.00}; no breakeven/trailing/time exit.", TargetDollars, StopDollars));
                Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] Directional filter: window={0:0.0}s, OFI threshold={1:0.0}, delta threshold={2:0.0}, continuation vote=3/4.", DirectionWindowSeconds, OfiThreshold, DeltaThreshold));
                Print("[NQDomOrderFlowProBiasFilteredV4] Attach to intended futures instrument with live Level-II enabled. Start in Sim.");
            }
        }
        #endregion

        #region Market depth
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Bid && e.MarketDataType != MarketDataType.Ask)
                return;

            DateTime now = DateTime.UtcNow;
            bool isBid = e.MarketDataType == MarketDataType.Bid;
            double price = Instrument.MasterInstrument.RoundToTickSize(e.Price);

            lock (bookLock)
            {
                if (e.IsReset)
                {
                    bids.Clear();
                    asks.Clear();
                    tape.Clear();
                    ofiEvents.Clear();
                    bidStackMarks.Clear();
                    askStackMarks.Clear();
                    bidPullMarks.Clear();
                    askPullMarks.Clear();
                    bidIcebergs.Clear();
                    askIcebergs.Clear();
                    suspectBidSpoofUntil = DateTime.MinValue;
                    suspectAskSpoofUntil = DateTime.MinValue;
                    lastBid = 0;
                    lastAsk = 0;
                    lastTradePrice = 0;
                    lastTradeDirection = 0;
                    return;
                }

                List<BookLevel> side = isBid ? bids : asks;
                Dictionary<double, IcebergTrack> iceSide = isBid ? bidIcebergs : askIcebergs;
                int pos = e.Position;

                if (pos < 0)
                    return;

                if (e.Operation == Operation.Add)
                {
                    BookLevel level = NewBookLevel(price, e.Volume, pos, now);
                    if (pos < side.Count)
                        side.Insert(pos, level);
                    else if (pos == side.Count)
                        side.Add(level);
                    else
                        return; // ignore gaps rather than corrupt ladder ordering

                    RecordOfiEvent(isBid, pos, e.Volume, now);
                    UpdateIcebergDepth(iceSide, price, e.Volume, now);
                    if (now >= realtimeStarted.AddSeconds(WarmupSeconds) && pos < StackingDepthLevels && e.Volume >= StackingOrderMinSize)
                        AddMark(isBid ? bidStackMarks : askStackMarks, price, e.Volume, now);
                }
                else if (e.Operation == Operation.Update)
                {
                    // Some feeds may begin an instance with an Update at the next
                    // sequential position. Append only when no index is skipped.
                    if (pos == side.Count)
                    {
                        side.Add(NewBookLevel(price, e.Volume, pos, now));
                        RecordOfiEvent(isBid, pos, e.Volume, now);
                        UpdateIcebergDepth(iceSide, price, e.Volume, now);
                        return;
                    }
                    if (pos < 0 || pos >= side.Count)
                        return;

                    BookLevel old = side[pos];
                    if (Math.Abs(old.Price - price) >= TickSize * 0.5)
                    {
                        // A repriced ladder slot is not treated as a cancellation:
                        // rank shifts and feed resynchronization must not fake spoof signals.
                        side[pos] = NewBookLevel(price, e.Volume, pos, now);
                        UpdateIcebergDepth(iceSide, price, e.Volume, now);
                    }
                    else
                    {
                        long sizeDelta = e.Volume - old.Size;
                        HandleSizeChange(isBid, old, e.Volume, pos, now);
                        RecordOfiEvent(isBid, pos, sizeDelta, now);
                        old.Size = e.Volume;
                        old.LastPosition = pos;
                        UpdateIcebergDepth(iceSide, price, e.Volume, now);
                    }
                }
                else if (e.Operation == Operation.Remove)
                {
                    if (pos >= side.Count)
                        return;

                    BookLevel old = side[pos];
                    HandleSizeChange(isBid, old, 0, pos, now);
                    RecordOfiEvent(isBid, pos, -old.Size, now);
                    side.RemoveAt(pos);
                }

                while (side.Count > 20)
                    side.RemoveAt(side.Count - 1);
                CleanupTransientState(now);
            }
        }

        // Event-based, weighted multi-level order-flow imbalance (OFI).
        // This is a practical DOM proxy, not a perfect reconstruction of exchange-level OFI.
        // Caller holds bookLock.
        private void RecordOfiEvent(bool isBid, int position, double sizeDelta, DateTime now)
        {
            if (position < 0 || position >= OfiDepthLevels || Math.Abs(sizeDelta) < 0.0001)
                return;

            double depthWeight = 1.0 / (position + 1.0);
            double sideSign = isBid ? 1.0 : -1.0;
            ofiEvents.Add(new FlowDelta
            {
                Time = now,
                SignedWeightedDelta = sideSign * sizeDelta * depthWeight
            });

            while (ofiEvents.Count > 0
                && (now - ofiEvents[0].Time).TotalSeconds > DirectionWindowSeconds)
                ofiEvents.RemoveAt(0);
        }

        private BookLevel NewBookLevel(double price, long size, int position, DateTime now)
        {
            return new BookLevel
            {
                Price = price,
                Size = size,
                PeakSize = size,
                TotalTraded = 0,
                TradedSinceChange = 0,
                FirstSeen = now,
                LastChange = now,
                LastPosition = position
            };
        }

        private void HandleSizeChange(bool isBid, BookLevel level, long newSize, int position, DateTime now)
        {
            if (level == null)
                return;

            long oldSize = level.Size;
            long delta = newSize - oldSize;
            bool signalWarm = realtimeStarted == DateTime.MinValue
                || now >= realtimeStarted.AddSeconds(WarmupSeconds);

            if (signalWarm && delta > 0 && position < StackingDepthLevels && delta >= StackingMinAdd && newSize >= StackingOrderMinSize)
                AddMark(isBid ? bidStackMarks : askStackMarks, level.Price, delta, now);

            if (delta < 0)
            {
                long drop = -delta;
                double tradedFracOfDrop = drop > 0 ? Math.Min(1.0, (double)level.TradedSinceChange / drop) : 1.0;
                if (signalWarm && position < PullDepthLevels && drop >= PullMinContracts && tradedFracOfDrop < 0.50)
                    AddMark(isBid ? bidPullMarks : askPullMarks, level.Price, drop, now);

                double age = (now - level.FirstSeen).TotalSeconds;
                double cancelFraction = level.PeakSize > 0 ? (double)drop / level.PeakSize : 0.0;
                double lifetimeTradedFraction = level.PeakSize > 0 ? (double)level.TotalTraded / level.PeakSize : 1.0;
                bool nearBest = IsNearBestUnsafe(isBid, level.Price, PullDepthLevels);
                bool suspectedSpoof = signalWarm
                    && nearBest
                    && position < PullDepthLevels
                    && level.PeakSize >= LargeOrderMinSize
                    && age <= SpoofLifetimeSeconds
                    && cancelFraction >= SpoofCancelFraction
                    && lifetimeTradedFraction <= SpoofMaxTradedFraction
                    && level.TradedSinceChange < Math.Max(1.0, drop * SpoofMaxTradedFraction);

                if (suspectedSpoof)
                {
                    if (isBid)
                        suspectBidSpoofUntil = now.AddSeconds(SpoofCooldownSeconds);
                    else
                        suspectAskSpoofUntil = now.AddSeconds(SpoofCooldownSeconds);

                    if (EnableDiagnostics)
                        Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] SUSPECTED PULL/SPOOF side={0} price={1} peak={2} drop={3} traded={4} age={5:0.00}s. Directional entries toward this side temporarily blocked.",
                            isBid ? "BID" : "ASK", level.Price, level.PeakSize, drop, level.TotalTraded, age));
                }
            }

            if (newSize > level.PeakSize)
                level.PeakSize = newSize;
            level.TradedSinceChange = 0;
            level.LastChange = now;
        }

        private bool IsNearBestUnsafe(bool isBid, double price, int maxLevels)
        {
            List<BookLevel> side = isBid ? bids : asks;
            if (side.Count == 0)
                return false;
            double best = side[0].Price;
            double dist = isBid ? (best - price) / TickSize : (price - best) / TickSize;
            return dist >= -0.1 && dist <= Math.Max(1, maxLevels);
        }
        #endregion

        #region Level I / tape
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            lock (bookLock)
            {
                if (e.MarketDataType == MarketDataType.Bid)
                {
                    lastBid = e.Price;
                    return;
                }
                if (e.MarketDataType == MarketDataType.Ask)
                {
                    lastAsk = e.Price;
                    return;
                }
                if (e.MarketDataType != MarketDataType.Last || e.Price <= 0 || e.Volume <= 0)
                    return;

                double p = Instrument.MasterInstrument.RoundToTickSize(e.Price);
                int aggressor = ClassifyAggressor(p);
                lastTradeDirection = aggressor != 0 ? aggressor : lastTradeDirection;
                lastTradePrice = p;

                TapeTrade trade = new TapeTrade { Time = now, Price = p, Size = e.Volume, Aggressor = aggressor };
                tape.Add(trade);
                TrimTapeUnsafe(now);

                if (aggressor < 0)
                {
                    UpdateTradeAtPrice(bids, p, e.Volume);
                    UpdateIcebergTrade(bidIcebergs, p, e.Volume, now);
                }
                else if (aggressor > 0)
                {
                    UpdateTradeAtPrice(asks, p, e.Volume);
                    UpdateIcebergTrade(askIcebergs, p, e.Volume, now);
                }
            }
        }

        private int ClassifyAggressor(double price)
        {
            // Prefer Level-I quotes when supplied by the feed, but fall back to
            // the live DOM best prices so the tape-delta filter does not silently
            // remain unclassified when separate L1 quote updates are unavailable.
            double effectiveAsk = lastAsk > 0 ? lastAsk : (asks.Count > 0 ? asks[0].Price : 0.0);
            double effectiveBid = lastBid > 0 ? lastBid : (bids.Count > 0 ? bids[0].Price : 0.0);

            if (effectiveAsk > 0 && price >= effectiveAsk - TickSize * 0.10)
                return 1;
            if (effectiveBid > 0 && price <= effectiveBid + TickSize * 0.10)
                return -1;
            if (lastTradePrice > 0)
            {
                if (price > lastTradePrice)
                    return 1;
                if (price < lastTradePrice)
                    return -1;
            }
            return lastTradeDirection;
        }

        private void UpdateTradeAtPrice(List<BookLevel> side, double price, long volume)
        {
            for (int i = 0; i < side.Count; i++)
            {
                if (Math.Abs(side[i].Price - price) < TickSize * 0.5)
                {
                    side[i].TotalTraded += volume;
                    side[i].TradedSinceChange += volume;
                    break;
                }
            }
        }

        private void TrimTapeUnsafe(DateTime now)
        {
            double keepSeconds = Math.Max(Math.Max(AbsorptionWindowSeconds, IcebergWindowSeconds), DirectionWindowSeconds) + 1.0;
            int removeCount = 0;
            while (removeCount < tape.Count && (now - tape[removeCount].Time).TotalSeconds > keepSeconds)
                removeCount++;
            if (removeCount > 0)
                tape.RemoveRange(0, removeCount);
        }
        #endregion

        #region Stacking, pulling and iceberg tracking
        private void AddMark(List<FlowMark> marks, double price, long size, DateTime now)
        {
            double rounded = Instrument.MasterInstrument.RoundToTickSize(price);
            // Avoid many copies of the same level in one short burst.
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                if ((now - marks[i].Time).TotalSeconds > Math.Max(StackingWindowSeconds, PullWindowSeconds))
                    break;
                if (Math.Abs(marks[i].Price - rounded) < TickSize * 0.5)
                {
                    marks[i].Time = now;
                    marks[i].Size += size;
                    return;
                }
            }
            marks.Add(new FlowMark { Time = now, Price = rounded, Size = size });
        }

        private void UpdateIcebergDepth(Dictionary<double, IcebergTrack> tracks, double price, long visibleSize, DateTime now)
        {
            double key = Instrument.MasterInstrument.RoundToTickSize(price);
            IcebergTrack tracker;
            if (!tracks.TryGetValue(key, out tracker) || (now - tracker.LastActivity).TotalSeconds > IcebergWindowSeconds)
            {
                tracker = new IcebergTrack
                {
                    FirstSeen = now,
                    LastActivity = now,
                    Executed = 0,
                    PeakVisible = visibleSize,
                    LastVisible = visibleSize,
                    Replenishments = 0,
                    WasReduced = false
                };
                tracks[key] = tracker;
                return;
            }

            if (visibleSize < tracker.LastVisible && tracker.LastVisible > 0
                && visibleSize <= tracker.LastVisible * 0.70)
                tracker.WasReduced = true;

            if (tracker.WasReduced && visibleSize > tracker.LastVisible
                && visibleSize >= tracker.PeakVisible * 0.70)
            {
                tracker.Replenishments++;
                tracker.WasReduced = false;
            }

            if (visibleSize > tracker.PeakVisible)
                tracker.PeakVisible = visibleSize;
            tracker.LastVisible = visibleSize;
            tracker.LastActivity = now;
        }

        private void UpdateIcebergTrade(Dictionary<double, IcebergTrack> tracks, double price, long volume, DateTime now)
        {
            double key = Instrument.MasterInstrument.RoundToTickSize(price);
            IcebergTrack tracker;
            if (!tracks.TryGetValue(key, out tracker) || (now - tracker.LastActivity).TotalSeconds > IcebergWindowSeconds)
            {
                tracker = new IcebergTrack
                {
                    FirstSeen = now,
                    LastActivity = now,
                    Executed = 0,
                    PeakVisible = GetDisplayedUnsafe(key, tracks == bidIcebergs),
                    LastVisible = GetDisplayedUnsafe(key, tracks == bidIcebergs),
                    Replenishments = 0,
                    WasReduced = false
                };
                tracks[key] = tracker;
            }
            tracker.Executed += volume;
            tracker.LastActivity = now;
            long displayed = GetDisplayedUnsafe(key, tracks == bidIcebergs);
            if (displayed > tracker.PeakVisible)
                tracker.PeakVisible = displayed;
            tracker.LastVisible = displayed;
        }

        private long GetDisplayedUnsafe(double price, bool isBid)
        {
            List<BookLevel> side = isBid ? bids : asks;
            for (int i = 0; i < side.Count; i++)
                if (Math.Abs(side[i].Price - price) < TickSize * 0.5)
                    return side[i].Size;
            return 0;
        }

        private void CleanupTransientState(DateTime now)
        {
            CleanupMarks(bidStackMarks, now, StackingWindowSeconds);
            CleanupMarks(askStackMarks, now, StackingWindowSeconds);
            CleanupMarks(bidPullMarks, now, PullWindowSeconds);
            CleanupMarks(askPullMarks, now, PullWindowSeconds);
            CleanupIcebergs(bidIcebergs, now);
            CleanupIcebergs(askIcebergs, now);
        }

        private void CleanupMarks(List<FlowMark> marks, DateTime now, double seconds)
        {
            for (int i = marks.Count - 1; i >= 0; i--)
                if ((now - marks[i].Time).TotalSeconds > seconds)
                    marks.RemoveAt(i);
        }

        private void CleanupIcebergs(Dictionary<double, IcebergTrack> tracks, DateTime now)
        {
            if (tracks.Count < 200)
                return;
            List<double> stale = new List<double>();
            foreach (KeyValuePair<double, IcebergTrack> pair in tracks)
                if ((now - pair.Value.LastActivity).TotalSeconds > IcebergWindowSeconds * 2)
                    stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++)
                tracks.Remove(stale[i]);
        }

        private int CountDistinctNearBest(List<FlowMark> marks, bool isBid, int depthLevels, DateTime now, double windowSeconds, long minSize)
        {
            List<BookLevel> side = isBid ? bids : asks;
            if (side.Count == 0)
                return 0;
            double best = side[0].Price;
            HashSet<double> prices = new HashSet<double>();
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                FlowMark mark = marks[i];
                if ((now - mark.Time).TotalSeconds > windowSeconds)
                    continue;
                if (mark.Size < minSize)
                    continue;
                double dist = isBid ? (best - mark.Price) / TickSize : (mark.Price - best) / TickSize;
                if (dist < -0.1 || dist > depthLevels)
                    continue;
                long visible = GetDisplayedUnsafe(mark.Price, isBid);
                if (visible >= Math.Max(1, StackingOrderMinSize * 0.5))
                    prices.Add(mark.Price);
                if (prices.Count >= 10)
                    break;
            }
            return prices.Count;
        }

        private bool HasPulling(List<FlowMark> marks, bool isBid, DateTime now)
        {
            List<BookLevel> side = isBid ? bids : asks;
            if (side.Count == 0)
                return false;
            double best = side[0].Price;
            HashSet<double> prices = new HashSet<double>();
            long totalPulled = 0;
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                FlowMark mark = marks[i];
                if ((now - mark.Time).TotalSeconds > PullWindowSeconds)
                    continue;
                double dist = isBid ? (best - mark.Price) / TickSize : (mark.Price - best) / TickSize;
                if (dist < -0.1 || dist > PullDepthLevels)
                    continue;
                prices.Add(mark.Price);
                totalPulled += mark.Size;
            }
            return prices.Count >= MinPullingLevels || totalPulled >= PullVolumeThreshold;
        }
        #endregion

        #region Signal calculations
        private FlowSnapshot TakeSnapshot(DateTime now)
        {
            lock (bookLock)
            {
                FlowSnapshot s = new FlowSnapshot();
                if (bids.Count == 0 || asks.Count == 0)
                {
                    s.InvalidReason = "DOM book has no bid or ask levels yet";
                    return s;
                }

                // The DOM book already contains best bid/ask. Do not require the
                // separate L1 OnMarketData fields to be populated before entries.
                s.BestBid = bids[0].Price;
                s.BestAsk = asks[0].Price;
                s.SpreadTicks = (s.BestAsk - s.BestBid) / TickSize;
                if (s.SpreadTicks < 0)
                {
                    s.InvalidReason = "invalid/crossed DOM: ask is below bid";
                    return s;
                }
                if (s.SpreadTicks > MaxSpreadTicks)
                {
                    s.InvalidReason = string.Format("spread {0:0.0} ticks exceeds MaxSpreadTicks={1}", s.SpreadTicks, MaxSpreadTicks);
                    return s;
                }

                int levels = Math.Min(ImbalanceLevels, Math.Min(bids.Count, asks.Count));
                double bidWeight = 0;
                double askWeight = 0;
                for (int i = 0; i < levels; i++)
                {
                    double weight = 1.0 / (i + 1.0);
                    bidWeight += Math.Max(0, bids[i].Size) * weight;
                    askWeight += Math.Max(0, asks[i].Size) * weight;
                }
                double total = bidWeight + askWeight;
                s.Imbalance = total > 0 ? (bidWeight - askWeight) / total : 0.0;
                ComputeDirectionalMetricsUnsafe(now, out s.OrderFlowImbalance, out s.NetAggressorDelta,
                    out s.PriceMoveTicks, out s.ClassifiedTrades);

                ComputeAbsorptionUnsafe(now, out s.BullAbsorption, out s.BearAbsorption);
                s.BidStacking = CountDistinctNearBest(bidStackMarks, true, StackingDepthLevels, now, StackingWindowSeconds, StackingMinAdd) >= MinStackingLevels;
                s.AskStacking = CountDistinctNearBest(askStackMarks, false, StackingDepthLevels, now, StackingWindowSeconds, StackingMinAdd) >= MinStackingLevels;

                s.SuspectBidSpoof = now < suspectBidSpoofUntil;
                s.SuspectAskSpoof = now < suspectAskSpoofUntil;
                s.BidPulling = HasPulling(bidPullMarks, true, now) && !s.SuspectBidSpoof;
                s.AskPulling = HasPulling(askPullMarks, false, now) && !s.SuspectAskSpoof;
                s.BidIceberg = HasIcebergUnsafe(bidIcebergs, true, s.BestBid, now);
                s.AskIceberg = HasIcebergUnsafe(askIcebergs, false, s.BestAsk, now);

                s.LongConfirmations = (s.BullAbsorption ? 1 : 0)
                    + (s.BidStacking ? 1 : 0)
                    + (s.AskPulling ? 1 : 0)
                    + (s.BidIceberg ? 1 : 0);
                s.ShortConfirmations = (s.BearAbsorption ? 1 : 0)
                    + (s.AskStacking ? 1 : 0)
                    + (s.BidPulling ? 1 : 0)
                    + (s.AskIceberg ? 1 : 0);
                BuildDirectionalBias(s);
                s.Valid = true;
                return s;
            }
        }

        private void ComputeDirectionalMetricsUnsafe(DateTime now, out double ofi, out double delta,
            out double priceMoveTicks, out int classifiedTrades)
        {
            ofi = 0.0;
            delta = 0.0;
            priceMoveTicks = 0.0;
            classifiedTrades = 0;

            int removeOfi = 0;
            for (int i = ofiEvents.Count - 1; i >= 0; i--)
            {
                if ((now - ofiEvents[i].Time).TotalSeconds > DirectionWindowSeconds)
                    break;
                ofi += ofiEvents[i].SignedWeightedDelta;
            }
            while (removeOfi < ofiEvents.Count
                && (now - ofiEvents[removeOfi].Time).TotalSeconds > DirectionWindowSeconds)
                removeOfi++;
            if (removeOfi > 0)
                ofiEvents.RemoveRange(0, removeOfi);

            bool gotAnyPrice = false;
            double newestPrice = 0.0;
            double oldestPrice = 0.0;
            bool gotClassifiedPrice = false;
            double newestClassifiedPrice = 0.0;
            double oldestClassifiedPrice = 0.0;

            // tape is chronological; scan newest-to-oldest to compute window delta and price response.
            for (int i = tape.Count - 1; i >= 0; i--)
            {
                TapeTrade t = tape[i];
                if ((now - t.Time).TotalSeconds > DirectionWindowSeconds)
                    break;

                if (!gotAnyPrice)
                {
                    newestPrice = t.Price;
                    oldestPrice = t.Price;
                    gotAnyPrice = true;
                }
                else
                    oldestPrice = t.Price;

                if (t.Aggressor > 0)
                    delta += t.Size;
                else if (t.Aggressor < 0)
                    delta -= t.Size;

                if (t.Aggressor != 0)
                {
                    classifiedTrades++;
                    if (!gotClassifiedPrice)
                    {
                        newestClassifiedPrice = t.Price;
                        oldestClassifiedPrice = t.Price;
                        gotClassifiedPrice = true;
                    }
                    else
                        oldestClassifiedPrice = t.Price;
                }
            }

            if (gotClassifiedPrice && classifiedTrades >= 2 && TickSize > 0)
                priceMoveTicks = (newestClassifiedPrice - oldestClassifiedPrice) / TickSize;
            else if (gotAnyPrice && TickSize > 0)
                priceMoveTicks = (newestPrice - oldestPrice) / TickSize;
        }

        private void BuildDirectionalBias(FlowSnapshot s)
        {
            bool ofiUp = s.OrderFlowImbalance >= OfiThreshold;
            bool ofiDown = s.OrderFlowImbalance <= -OfiThreshold;
            bool deltaUp = s.NetAggressorDelta >= DeltaThreshold;
            bool deltaDown = s.NetAggressorDelta <= -DeltaThreshold;
            bool priceUp = s.PriceMoveTicks >= MinPriceResponseTicks;
            bool priceDown = s.PriceMoveTicks <= -MinPriceResponseTicks;
            bool bookUp = s.Imbalance >= ImbalanceThreshold;
            bool bookDown = s.Imbalance <= -ImbalanceThreshold;

            s.LongBiasScore = (ofiUp ? 1 : 0) + (deltaUp ? 1 : 0) + (priceUp ? 1 : 0) + (bookUp ? 1 : 0);
            s.ShortBiasScore = (ofiDown ? 1 : 0) + (deltaDown ? 1 : 0) + (priceDown ? 1 : 0) + (bookDown ? 1 : 0);

            // Continuation: at least three of four independent readings align, with
            // at least one event-flow input and no strong price/delta contradiction.
            bool continuationLong = s.LongBiasScore >= 3
                && (ofiUp || deltaUp)
                && s.NetAggressorDelta > -DeltaThreshold
                && s.PriceMoveTicks >= -MaxAbsorptionCounterMoveTicks;
            bool continuationShort = s.ShortBiasScore >= 3
                && (ofiDown || deltaDown)
                && s.NetAggressorDelta < DeltaThreshold
                && s.PriceMoveTicks <= MaxAbsorptionCounterMoveTicks;

            // Reversal/absorption path: aggressive flow is absorbed, OFI tilts toward
            // the defending side, price stays contained, and at least one DOM structure
            // clue backs the interpretation. Do not mistake negative delta alone for SELL
            // when seller aggression is being absorbed at the bid (or vice versa).
            bool absorptionLong = s.BullAbsorption
                && ofiUp
                && Math.Abs(s.PriceMoveTicks) <= MaxAbsorptionCounterMoveTicks
                && (s.BidStacking || s.BidIceberg || s.AskPulling);
            bool absorptionShort = s.BearAbsorption
                && ofiDown
                && Math.Abs(s.PriceMoveTicks) <= MaxAbsorptionCounterMoveTicks
                && (s.AskStacking || s.AskIceberg || s.BidPulling);

            bool longCandidate = (continuationLong || absorptionLong) && !s.SuspectBidSpoof;
            bool shortCandidate = (continuationShort || absorptionShort) && !s.SuspectAskSpoof;

            // Ambiguous or contradictory evidence means NO TRADE.
            if (longCandidate && !shortCandidate)
            {
                s.BuyBias = true;
                s.SellBias = false;
                s.BiasReason = absorptionLong ? "BULLISH_ABSORPTION_REVERSAL" : "BULLISH_OFI_DELTA_PRICE_ALIGNMENT";
            }
            else if (shortCandidate && !longCandidate)
            {
                s.BuyBias = false;
                s.SellBias = true;
                s.BiasReason = absorptionShort ? "BEARISH_ABSORPTION_REVERSAL" : "BEARISH_OFI_DELTA_PRICE_ALIGNMENT";
            }
            else
            {
                s.BuyBias = false;
                s.SellBias = false;
                if (s.SuspectBidSpoof || s.SuspectAskSpoof)
                    s.BiasReason = "SUSPECTED_SPOOF_VETO";
                else if (longCandidate && shortCandidate)
                    s.BiasReason = "CONFLICTING_DIRECTIONAL_EVIDENCE";
                else
                    s.BiasReason = "INSUFFICIENT_OFI_DELTA_PRICE_ALIGNMENT";
            }
        }

        private void ComputeAbsorptionUnsafe(DateTime now, out bool bull, out bool bear)
        {
            bull = false;
            bear = false;
            double low = double.MaxValue;
            double high = double.MinValue;
            long buyVolume = 0;
            long sellVolume = 0;
            int classified = 0;

            for (int i = tape.Count - 1; i >= 0; i--)
            {
                TapeTrade t = tape[i];
                if ((now - t.Time).TotalSeconds > AbsorptionWindowSeconds)
                    break;
                low = Math.Min(low, t.Price);
                high = Math.Max(high, t.Price);
                if (t.Aggressor > 0) buyVolume += t.Size;
                else if (t.Aggressor < 0) sellVolume += t.Size;
                if (t.Aggressor != 0) classified++;
            }

            if (classified < 3 || low == double.MaxValue || high == double.MinValue)
                return;
            double rangeTicks = (high - low) / TickSize;
            if (rangeTicks > AbsorptionMaxMoveTicks)
                return;

            // Seller aggression absorbed at the bid is bullish; buyer aggression
            // absorbed at the ask is bearish. This is a heuristic, not proof.
            if (sellVolume >= AbsorptionMinVolume && sellVolume >= buyVolume * AbsorptionDominance)
                bull = true;
            if (buyVolume >= AbsorptionMinVolume && buyVolume >= sellVolume * AbsorptionDominance)
                bear = true;
        }

        private bool HasIcebergUnsafe(Dictionary<double, IcebergTrack> tracks, bool isBid, double bestPrice, DateTime now)
        {
            foreach (KeyValuePair<double, IcebergTrack> pair in tracks)
            {
                IcebergTrack t = pair.Value;
                if ((now - t.LastActivity).TotalSeconds > IcebergWindowSeconds)
                    continue;
                double dist = Math.Abs(pair.Key - bestPrice) / TickSize;
                if (dist > 1.0)
                    continue;
                long displayed = GetDisplayedUnsafe(pair.Key, isBid);
                if (displayed <= 0)
                    continue;
                if (t.Executed >= IcebergMinExecuted
                    && t.PeakVisible > 0
                    && t.Executed >= t.PeakVisible * IcebergMultiple
                    && t.Replenishments >= IcebergMinReplenishments)
                    return true;
            }
            return false;
        }

        private void ProcessSignal(FlowSnapshot s, DateTime now)
        {
            if (!s.Valid)
                return;

            cntEval++;
            double mid = (s.BestBid + s.BestAsk) * 0.5;
            UpdateMidHistory(mid, now);

            bool isReversal = s.BiasReason != null
                && s.BiasReason.IndexOf("ABSORPTION_REVERSAL", StringComparison.Ordinal) >= 0;

            // Base candidates: directional bias + spoof veto.
            bool longRaw = s.BuyBias && !s.SuspectBidSpoof;
            bool shortRaw = s.SellBias && !s.SuspectAskSpoof;
            if (longRaw || shortRaw)
                cntCandidate++;

            // GATE 1 - range/compression (volatility floor on the chart bars).
            if (EnableRangeFilter && rangeFilterBlocked)
            {
                if (longRaw || shortRaw)
                    cntBlkRange++;
                longRaw = false;
                shortRaw = false;
            }

            // GATE 2 - 5-minute trend alignment. CONTINUATION entries only (unless
            // ApplyHtfGateToReversal), because a reversal buy by definition happens below a falling EMA.
            if ((longRaw || shortRaw) && EnableHigherTimeframeTrendFilter
                && (!isReversal || ApplyHtfGateToReversal)
                && CurrentBars.Length > 1 && trendEma5m != null
                && CurrentBars[1] >= TrendEmaPeriod + 3)
            {
                int back = HtfUseCompletedBars ? 1 : 0;
                double cl = Closes[1][back];
                double emaNow = trendEma5m[back];
                double emaPrev = trendEma5m[back + 1];
                bool trendUp = cl > emaNow && emaNow > emaPrev;
                bool trendDown = cl < emaNow && emaNow < emaPrev;
                bool blockedHtf = false;
                if (longRaw && !trendUp) { longRaw = false; blockedHtf = true; }
                if (shortRaw && !trendDown) { shortRaw = false; blockedHtf = true; }
                if (blockedHtf)
                    cntBlkHtf++;
            }

            // GATE 3 - no-chase: how far did price already travel IN THE TRADE DIRECTION during the
            // last ChaseLookbackSeconds (rolling, independent of the chart bar clock).
            if ((longRaw || shortRaw) && MaxChaseTicks > 0 && TickSize > 0 && midHistory.Count > 0)
            {
                double upTicks = (mid - midHistory[0].Price) / TickSize;
                bool chased = false;
                if (longRaw && upTicks > MaxChaseTicks) { longRaw = false; chased = true; }
                if (shortRaw && -upTicks > MaxChaseTicks) { shortRaw = false; chased = true; }
                if (chased)
                    cntBlkChase++;
            }

            // GATE 4 - opposite-flip confirmation only (BUY<->SELL). NONE->BUY persistence is
            // handled by ConfirmSeconds below, so the two timers no longer stack.
            string rawDirection = longRaw ? "BUY" : (shortRaw ? "SELL" : "NONE");
            if (rawDirection != "NONE")
            {
                if (lastNonNoneDirection.Length > 0
                    && (now - lastNonNoneAt).TotalSeconds <= 30.0
                    && rawDirection != lastNonNoneDirection)
                    lastBiasDirectionChange = now;
                lastNonNoneDirection = rawDirection;
                lastNonNoneAt = now;

                if (lastBiasDirectionChange != DateTime.MinValue
                    && (now - lastBiasDirectionChange).TotalSeconds < BiasFlipConfirmSeconds)
                {
                    cntBlkFlip++;
                    longRaw = false;
                    shortRaw = false;
                }
            }
            lastRawDirection = rawDirection;

            if (longRaw) lastLongRawAt = now;
            if (shortRaw) lastShortRawAt = now;

            if (EnableDiagnostics && (lastDiagnosticsAt == DateTime.MinValue || (now - lastDiagnosticsAt).TotalSeconds >= DiagnosticsSeconds))
            {
                string gate;
                if (realtimeStarted != DateTime.MinValue && (now - realtimeStarted).TotalSeconds < WarmupSeconds)
                    gate = "WARMUP";
                else if (Position.MarketPosition != MarketPosition.Flat)
                    gate = "POSITION_OPEN";
                else if (entryPending)
                    gate = "ENTRY_PENDING";
                else if (!signalArmed)
                    gate = "WAITING_FOR_NEUTRAL_RESET";
                else if (longRaw)
                    gate = "LONG_CANDIDATE";
                else if (shortRaw)
                    gate = "SHORT_CANDIDATE";
                else if (s.SuspectBidSpoof || s.SuspectAskSpoof)
                    gate = "BLOCKED_BY_SUSPECTED_SPOOF_OR_NO_DIRECTION";
                else
                    gate = "NO_DIRECTIONAL_PRESSURE_OR_CONFIRMATION";

                Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] DOM bid={0} ask={1} spread={2:0.0}/{3} imb={4:0.00} OFI={5:0.0}/{6:0.0} Delta={7:0}/{8:0} dP={9:0.0}t nTrades={10} | bias={11} reason={12} votes L/S={13}/{14} | Abs B/S={15}/{16} Stack B/A={17}/{18} Pull B/A={19}/{20} Ice B/A={21}/{22} Spoof B/A={23}/{24} | triggers L/S={25}/{26} raw L/S={27}/{28} rangeBlocked={34} rangeTicks={35:0.0} armed={29} pending={30} pos={31} tradesToday={32} gate={33}",
                    s.BestBid, s.BestAsk, s.SpreadTicks, MaxSpreadTicks, s.Imbalance,
                    s.OrderFlowImbalance, OfiThreshold, s.NetAggressorDelta, DeltaThreshold, s.PriceMoveTicks, s.ClassifiedTrades,
                    s.BuyBias ? "BUY" : (s.SellBias ? "SELL" : "NO_TRADE"), s.BiasReason, s.LongBiasScore, s.ShortBiasScore,
                    s.BullAbsorption, s.BearAbsorption, s.BidStacking, s.AskStacking,
                    s.BidPulling, s.AskPulling, s.BidIceberg, s.AskIceberg,
                    s.SuspectBidSpoof, s.SuspectAskSpoof, s.LongConfirmations, s.ShortConfirmations,
                    longRaw, shortRaw, signalArmed, entryPending, Position.MarketPosition, tradesToday, gate, rangeFilterBlocked, recentRangeTicks));
                Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] GATES cumulative: evals={0} biasCandidates={1} | removed by: range={2} trend5m={3} chase={4} flip={5} | entries={6} unfilled={7} | style={8}",
                    cntEval, cntCandidate, cntBlkRange, cntBlkHtf, cntBlkChase, cntBlkFlip, cntEntries, cntUnfilled,
                    s.BiasReason));
                lastDiagnosticsAt = now;
            }

            if (realtimeStarted != DateTime.MinValue && (now - realtimeStarted).TotalSeconds < WarmupSeconds)
                return;

            if (Position.MarketPosition != MarketPosition.Flat || entryPending)
            {
                neutralSince = DateTime.MinValue;
                longCandidateSince = DateTime.MinValue;
                shortCandidateSince = DateTime.MinValue;
                return;
            }

            if (!longRaw && !shortRaw)
            {
                // A very short dropout of the signal must not restart the persistence timer.
                bool withinGrace = ConfirmGraceSeconds > 0.0
                    && ((longCandidateSince != DateTime.MinValue && (now - lastLongRawAt).TotalSeconds <= ConfirmGraceSeconds)
                        || (shortCandidateSince != DateTime.MinValue && (now - lastShortRawAt).TotalSeconds <= ConfirmGraceSeconds));
                if (!withinGrace)
                {
                    longCandidateSince = DateTime.MinValue;
                    shortCandidateSince = DateTime.MinValue;
                }
                if (neutralSince == DateTime.MinValue)
                    neutralSince = now;
                if ((now - neutralSince).TotalSeconds >= NeutralResetSeconds)
                {
                    if (!signalArmed && EnableDiagnostics)
                        Print("[NQDomOrderFlowProBiasFilteredV4] SIGNAL RE-ARMED after neutral reset.");
                    signalArmed = true;
                }
                return;
            }

            neutralSince = DateTime.MinValue;
            if (!signalArmed)
                return;

            if (longRaw)
            {
                shortCandidateSince = DateTime.MinValue;
                if (longCandidateSince == DateTime.MinValue)
                    longCandidateSince = now;
                if ((now - longCandidateSince).TotalSeconds >= ConfirmSeconds)
                    SubmitEntry(true, s, now, isReversal);
            }
            else if (shortRaw)
            {
                longCandidateSince = DateTime.MinValue;
                if (shortCandidateSince == DateTime.MinValue)
                    shortCandidateSince = now;
                if ((now - shortCandidateSince).TotalSeconds >= ConfirmSeconds)
                    SubmitEntry(false, s, now, isReversal);
            }
        }

        private void UpdateMidHistory(double mid, DateTime now)
        {
            if (mid <= 0.0)
                return;
            if (lastMidSampleAt == DateTime.MinValue || (now - lastMidSampleAt).TotalMilliseconds >= 100.0)
            {
                MidSample m = new MidSample();
                m.Time = now;
                m.Price = mid;
                midHistory.Add(m);
                lastMidSampleAt = now;
            }
            int remove = 0;
            while (remove < midHistory.Count - 1 && (now - midHistory[remove].Time).TotalSeconds > ChaseLookbackSeconds)
                remove++;
            if (remove > 0)
                midHistory.RemoveRange(0, remove);
        }

        private void SubmitEntry(bool isLong, FlowSnapshot s, DateTime now, bool isReversal)
        {
            if (entryPending || Position.MarketPosition != MarketPosition.Flat)
                return;
            if (lastEntryAt != DateTime.MinValue && (now - lastEntryAt).TotalSeconds < EntryCooldownSeconds)
                return;
            if (lastUnfilledCancelAt != DateTime.MinValue && (now - lastUnfilledCancelAt).TotalSeconds < RetryCooldownSeconds)
                return;
            if (MaxTradesPerDay > 0 && tradesToday >= MaxTradesPerDay)
            {
                if (EnableDiagnostics)
                    Print("[NQDomOrderFlowProBiasFilteredV4] ENTRY BLOCKED: MaxTradesPerDay reached. No further entries today.");
                signalArmed = false;
                return;
            }
            if (s.BestBid <= 0 || s.BestAsk <= 0 || s.BestBid >= s.BestAsk)
                return;

            double limitPrice;
            double timeout;
            string style;
            if (isReversal)
            {
                // Absorption reversal: passive limit at the level being defended.
                limitPrice = isLong ? s.BestBid - (LimitEntryOffsetTicks * TickSize)
                                    : s.BestAsk + (LimitEntryOffsetTicks * TickSize);
                timeout = LimitOrderTimeoutSeconds;
                style = "PASSIVE";
            }
            else
            {
                // Continuation: marketable limit. Fills like a market order, but never worse than
                // ContinuationSlipTicks beyond the touch. Passive joining of the touch fills mostly
                // when the move fails (adverse selection).
                limitPrice = isLong ? s.BestAsk + (ContinuationSlipTicks * TickSize)
                                    : s.BestBid - (ContinuationSlipTicks * TickSize);
                timeout = ContinuationTimeoutSeconds;
                style = "MARKETABLE";
            }
            limitPrice = Instrument.MasterInstrument.RoundToTickSize(limitPrice);

            entryPending = true;
            pendingEntryOrder = null;
            pendingEntrySubmittedAt = now;
            pendingTimeoutSeconds = timeout;
            pendingIsReversal = isReversal;
            entryCancelRequested = false;
            signalArmed = false;
            // NOTE: lastEntryAt (the cooldown) is now set on the FILL, not on submission.
            longCandidateSince = DateTime.MinValue;
            shortCandidateSince = DateTime.MinValue;
            activeEntrySignal = isLong ? "OFLongV4" : "OFShortV4";
            cntEntries++;

            if (EnableDiagnostics)
                Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] ENTRY REQUEST {0} {1} qty={2} bias={3} reason={4} imb={5:0.00} OFI={6:0.0} Delta={7:0} dP={8:0.0}t target=${9:0.00} stop=${10:0.00} limit={11} timeout={12:0.0}s",
                    isLong ? "LONG" : "SHORT", style, Contracts, isLong ? "BUY" : "SELL", s.BiasReason,
                    s.Imbalance, s.OrderFlowImbalance, s.NetAggressorDelta, s.PriceMoveTicks, TargetDollars, StopDollars, limitPrice, timeout));

            if (isLong)
                EnterLongLimit(0, true, Contracts, limitPrice, "OFLongV4");
            else
                EnterShortLimit(0, true, Contracts, limitPrice, "OFShortV4");
        }
        #endregion

        private void CancelExpiredEntryLimit(DateTime now)
        {
            Order orderToCancel = null;
            lock (signalLock)
            {
                if (entryPending && !entryCancelRequested && pendingEntryOrder != null
                    && pendingEntrySubmittedAt != DateTime.MinValue
                    && (now - pendingEntrySubmittedAt).TotalSeconds >= pendingTimeoutSeconds)
                {
                    if (pendingEntryOrder.OrderState == OrderState.Accepted
                        || pendingEntryOrder.OrderState == OrderState.Working
                        || pendingEntryOrder.OrderState == OrderState.Submitted
                        || pendingEntryOrder.OrderState == OrderState.PartFilled)
                    {
                        entryCancelRequested = true;
                        orderToCancel = pendingEntryOrder;
                    }
                }
            }

            if (orderToCancel != null)
            {
                if (EnableDiagnostics)
                    Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] LIMIT TIMEOUT after {0:0.0}s; cancelling unfilled remainder.", pendingTimeoutSeconds));
                CancelOrder(orderToCancel);
            }
        }

        #region Strategy callbacks
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime)
                return;
            if (BarsInProgress == 1)
                return; // 5-minute series supplies trend context only.
            if (BarsInProgress != 0 || CurrentBar < BarsRequiredToTrade)
                return;

            DateTime barDate = Time[0].Date;
            lock (signalLock)
            {
                if (tradeDate == DateTime.MinValue)
                    tradeDate = barDate;
                else if (barDate != tradeDate)
                {
                    tradeDate = barDate;
                    tradesToday = 0;
                }
            }

            CancelExpiredEntryLimit(DateTime.UtcNow);

            // Estimate recent price compression from completed/current chart bars.
            // Best used on a 1-minute chart; tune only after Sim testing.
            recentRangeTicks = 0.0;
            if (CurrentBar >= RangeLookbackBars - 1 && TickSize > 0)
            {
                double hi = double.MinValue;
                double lo = double.MaxValue;
                for (int i = 0; i < RangeLookbackBars; i++)
                {
                    if (High[i] > hi) hi = High[i];
                    if (Low[i] < lo) lo = Low[i];
                }
                recentRangeTicks = (hi - lo) / TickSize;
            }
            rangeFilterBlocked = EnableRangeFilter
                && CurrentBar >= RangeLookbackBars - 1
                && recentRangeTicks < MinimumRangeTicks;

            DateTime now = DateTime.UtcNow;
            FlowSnapshot snapshot = TakeSnapshot(now);
            if (!snapshot.Valid)
            {
                if (EnableDiagnostics && (lastDiagnosticsAt == DateTime.MinValue || (now - lastDiagnosticsAt).TotalSeconds >= DiagnosticsSeconds))
                {
                    Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] WAITING FOR VALID DOM: {0}. Check that bid/ask depth is streaming and the spread is acceptable.",
                        string.IsNullOrEmpty(snapshot.InvalidReason) ? "book not ready" : snapshot.InvalidReason));
                    lastDiagnosticsAt = now;
                }
                return;
            }
            lock (signalLock)
                ProcessSignal(snapshot, now);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            lock (signalLock)
            {
                bool isEntry = order.Name == "OFLongV4" || order.Name == "OFShortV4";
                if (isEntry)
                {
                    pendingEntryOrder = order;
                    if (orderState == OrderState.Filled)
                    {
                        entryPending = false;
                        pendingEntryOrder = null;
                        pendingEntrySubmittedAt = DateTime.MinValue;
                        entryCancelRequested = false;
                        lastEntryAt = DateTime.UtcNow;   // cooldown starts at the FILL
                        tradesToday++;
                        if (EnableDiagnostics)
                            Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] ENTRY FILLED {0} filled={1} avg={2} tradesToday={3}", order.Name, filled, averageFillPrice, tradesToday));
                    }
                    else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                    {
                        if (orderState == OrderState.Cancelled && filled == 0)
                        {
                            // nothing was filled: do not burn the signal. Re-arm after a short retry delay.
                            signalArmed = true;
                            lastUnfilledCancelAt = DateTime.UtcNow;
                            cntUnfilled++;
                        }
                        entryPending = false;
                        pendingEntryOrder = null;
                        pendingEntrySubmittedAt = DateTime.MinValue;
                        entryCancelRequested = false;
                        if (EnableDiagnostics)
                            Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] ENTRY {0}: state={1}, filled={2}, error={3}, comment={4}",
                                order.Name, orderState, filled, error, comment));
                    }
                }

                if ((order.Name == "Stop loss" || order.Name == "Profit target") && orderState == OrderState.Filled && EnableDiagnostics)
                    Print(string.Format("[NQDomOrderFlowProBiasFilteredV4] EXIT FILLED: {0}, avg={1}, filled={2}", order.Name, averageFillPrice, filled));
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            lock (signalLock)
            {
                if (marketPosition != MarketPosition.Flat)
                {
                    hadPosition = true;
                    return;
                }

                // Only reset the arm state when a position really returned to flat.
                // A later signal must go neutral, then re-form, to avoid repeated
                // entries from the same persistent DOM condition.
                if (hadPosition)
                {
                    hadPosition = false;
                    entryPending = false;
                    pendingEntryOrder = null;
                    pendingEntrySubmittedAt = DateTime.MinValue;
                    entryCancelRequested = false;
                    activeEntrySignal = string.Empty;
                    signalArmed = false;
                    neutralSince = DateTime.MinValue;
                    longCandidateSince = DateTime.MinValue;
                    shortCandidateSince = DateTime.MinValue;
                    if (EnableDiagnostics)
                        Print("[NQDomOrderFlowProBiasFilteredV4] POSITION FLAT. Waiting for neutral reset before another entry.");
                }
            }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Contracts", GroupName = "1. Risk", Order = 1)]
        public int Contracts { get; set; }

        [NinjaScriptProperty, Range(1.0, 100000.0)]
        [Display(Name = "Target ($, position total)", GroupName = "1. Risk", Order = 2)]
        public double TargetDollars { get; set; }

        [NinjaScriptProperty, Range(1.0, 100000.0)]
        [Display(Name = "Stop ($, position total)", GroupName = "1. Risk", Order = 3)]
        public double StopDollars { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Imbalance levels", GroupName = "2. Imbalance", Order = 1)]
        public int ImbalanceLevels { get; set; }

        [NinjaScriptProperty, Range(0.05, 0.90)]
        [Display(Name = "Imbalance threshold", GroupName = "2. Imbalance", Order = 2)]
        public double ImbalanceThreshold { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Maximum spread (ticks)", GroupName = "2. Imbalance", Order = 3)]
        public int MaxSpreadTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 15.0)]
        [Display(Name = "Directional window (sec)", GroupName = "3. Directional Bias", Order = 1)]
        public double DirectionWindowSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "OFI depth levels", GroupName = "3. Directional Bias", Order = 2)]
        public int OfiDepthLevels { get; set; }

        [NinjaScriptProperty, Range(1.0, 10000.0)]
        [Display(Name = "Minimum weighted OFI", GroupName = "3. Directional Bias", Order = 3)]
        public double OfiThreshold { get; set; }

        [NinjaScriptProperty, Range(1.0, 10000.0)]
        [Display(Name = "Minimum net aggressor delta", GroupName = "3. Directional Bias", Order = 4)]
        public double DeltaThreshold { get; set; }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Minimum price response (ticks)", GroupName = "3. Directional Bias", Order = 5)]
        public int MinPriceResponseTicks { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Max counter-move for absorption bias (ticks)", GroupName = "3. Directional Bias", Order = 6)]
        public int MaxAbsorptionCounterMoveTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 30.0)]
        [Display(Name = "Absorption window (sec)", GroupName = "4. Absorption", Order = 1)]
        public double AbsorptionWindowSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 10000)]
        [Display(Name = "Minimum aggressive volume", GroupName = "4. Absorption", Order = 2)]
        public int AbsorptionMinVolume { get; set; }

        [NinjaScriptProperty, Range(1.0, 10.0)]
        [Display(Name = "Aggressor dominance", GroupName = "4. Absorption", Order = 3)]
        public double AbsorptionDominance { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Maximum price range (ticks)", GroupName = "4. Absorption", Order = 4)]
        public int AbsorptionMaxMoveTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 15.0)]
        [Display(Name = "Stacking window (sec)", GroupName = "5. Stacking / Pulling", Order = 1)]
        public double StackingWindowSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Stacking depth levels", GroupName = "5. Stacking / Pulling", Order = 2)]
        public int StackingDepthLevels { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Minimum size added", GroupName = "5. Stacking / Pulling", Order = 3)]
        public int StackingMinAdd { get; set; }

        [NinjaScriptProperty, Range(1, 10000)]
        [Display(Name = "Minimum visible stacked size", GroupName = "5. Stacking / Pulling", Order = 4)]
        public int StackingOrderMinSize { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Required stacked levels", GroupName = "5. Stacking / Pulling", Order = 5)]
        public int MinStackingLevels { get; set; }

        [NinjaScriptProperty, Range(0.5, 10.0)]
        [Display(Name = "Pulling window (sec)", GroupName = "5. Stacking / Pulling", Order = 6)]
        public double PullWindowSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Pulling depth levels", GroupName = "5. Stacking / Pulling", Order = 7)]
        public int PullDepthLevels { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Minimum contracts removed", GroupName = "5. Stacking / Pulling", Order = 8)]
        public int PullMinContracts { get; set; }

        [NinjaScriptProperty, Range(1, 10000)]
        [Display(Name = "Total pull volume threshold", GroupName = "5. Stacking / Pulling", Order = 9)]
        public int PullVolumeThreshold { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Required pulling levels", GroupName = "5. Stacking / Pulling", Order = 10)]
        public int MinPullingLevels { get; set; }

        [NinjaScriptProperty, Range(0.5, 30.0)]
        [Display(Name = "Iceberg window (sec)", GroupName = "6. Iceberg / Spoof", Order = 1)]
        public double IcebergWindowSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 10000)]
        [Display(Name = "Minimum iceberg executed volume", GroupName = "6. Iceberg / Spoof", Order = 2)]
        public int IcebergMinExecuted { get; set; }

        [NinjaScriptProperty, Range(1.0, 10.0)]
        [Display(Name = "Executed / displayed multiple", GroupName = "6. Iceberg / Spoof", Order = 3)]
        public double IcebergMultiple { get; set; }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Minimum replenishments", GroupName = "6. Iceberg / Spoof", Order = 4)]
        public int IcebergMinReplenishments { get; set; }

        [NinjaScriptProperty, Range(1, 10000)]
        [Display(Name = "Large order size", GroupName = "6. Iceberg / Spoof", Order = 5)]
        public int LargeOrderMinSize { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Suspected spoof lifetime (sec)", GroupName = "6. Iceberg / Spoof", Order = 6)]
        public double SpoofLifetimeSeconds { get; set; }

        [NinjaScriptProperty, Range(0.1, 1.0)]
        [Display(Name = "Suspected cancel fraction", GroupName = "6. Iceberg / Spoof", Order = 7)]
        public double SpoofCancelFraction { get; set; }

        [NinjaScriptProperty, Range(0.0, 1.0)]
        [Display(Name = "Maximum traded fraction for spoof flag", GroupName = "6. Iceberg / Spoof", Order = 8)]
        public double SpoofMaxTradedFraction { get; set; }

        [NinjaScriptProperty, Range(0.1, 30.0)]
        [Display(Name = "Spoof cooldown (sec)", GroupName = "6. Iceberg / Spoof", Order = 9)]
        public double SpoofCooldownSeconds { get; set; }

        [NinjaScriptProperty, Range(0.1, 10.0)]
        [Display(Name = "Signal confirmation (sec)", GroupName = "7. Entry Control", Order = 1)]
        public double ConfirmSeconds { get; set; }

        [NinjaScriptProperty, Range(0.1, 30.0)]
        [Display(Name = "Neutral reset before re-entry (sec)", GroupName = "7. Entry Control", Order = 2)]
        public double NeutralResetSeconds { get; set; }

        [NinjaScriptProperty, Range(0.0, 3600.0)]
        [Display(Name = "Cooldown between entries (sec)", GroupName = "7. Entry Control", Order = 3)]
        public double EntryCooldownSeconds { get; set; }

        [NinjaScriptProperty, Range(0.0, 120.0)]
        [Display(Name = "DOM warmup after enabling (sec)", GroupName = "7. Entry Control", Order = 4)]
        public double WarmupSeconds { get; set; }

        [NinjaScriptProperty, Range(1, 1000)]
        [Display(Name = "Maximum entries per calendar day", GroupName = "7. Entry Control", Order = 5)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable range/compression filter", GroupName = "7. Entry Control", Order = 6)]
        public bool EnableRangeFilter { get; set; }

        [NinjaScriptProperty, Range(3, 60)]
        [Display(Name = "Range lookback bars", GroupName = "7. Entry Control", Order = 7)]
        public int RangeLookbackBars { get; set; }

        [NinjaScriptProperty, Range(1, 200)]
        [Display(Name = "Minimum range over lookback (ticks)", GroupName = "7. Entry Control", Order = 8)]
        public int MinimumRangeTicks { get; set; }

        [NinjaScriptProperty, Range(0.0, 30.0)]
        [Display(Name = "Opposite-flip confirmation (sec)", GroupName = "7. Entry Control", Order = 9)]
        public double BiasFlipConfirmSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable 5-minute trend filter", GroupName = "7. Entry Control", Order = 10)]
        public bool EnableHigherTimeframeTrendFilter { get; set; }

        [NinjaScriptProperty, Range(5, 100)]
        [Display(Name = "5-minute EMA period", GroupName = "7. Entry Control", Order = 11)]
        public int TrendEmaPeriod { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "No-chase: max move in trade direction over lookback (ticks; 0 disables)", GroupName = "7. Entry Control", Order = 12)]
        public int MaxChaseTicks { get; set; }

        [NinjaScriptProperty, Range(1.0, 60.0)]
        [Display(Name = "Reversal limit timeout (sec)", GroupName = "7. Entry Control", Order = 13)]
        public double LimitOrderTimeoutSeconds { get; set; }

        [NinjaScriptProperty, Range(0, 10)]
        [Display(Name = "Reversal limit offset (ticks; long below bid / short above ask)", GroupName = "7. Entry Control", Order = 14)]
        public int LimitEntryOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(3.0, 120.0)]
        [Display(Name = "No-chase lookback (sec)", GroupName = "7. Entry Control", Order = 15)]
        public double ChaseLookbackSeconds { get; set; }

        [NinjaScriptProperty, Range(0.0, 5.0)]
        [Display(Name = "Signal dropout grace (sec)", GroupName = "7. Entry Control", Order = 16)]
        public double ConfirmGraceSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Apply 5-minute filter to reversal entries", GroupName = "7. Entry Control", Order = 17)]
        public bool ApplyHtfGateToReversal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "5-minute filter uses completed bars only", GroupName = "7. Entry Control", Order = 18)]
        public bool HtfUseCompletedBars { get; set; }

        [NinjaScriptProperty, Range(0, 10)]
        [Display(Name = "Continuation marketable-limit slippage (ticks)", GroupName = "7. Entry Control", Order = 19)]
        public int ContinuationSlipTicks { get; set; }

        [NinjaScriptProperty, Range(0.5, 30.0)]
        [Display(Name = "Continuation limit timeout (sec)", GroupName = "7. Entry Control", Order = 20)]
        public double ContinuationTimeoutSeconds { get; set; }

        [NinjaScriptProperty, Range(0.0, 60.0)]
        [Display(Name = "Retry delay after unfilled order (sec)", GroupName = "7. Entry Control", Order = 21)]
        public double RetryCooldownSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable diagnostics", GroupName = "8. Diagnostics", Order = 1)]
        public bool EnableDiagnostics { get; set; }

        [NinjaScriptProperty, Range(1.0, 120.0)]
        [Display(Name = "Diagnostics interval (sec)", GroupName = "8. Diagnostics", Order = 2)]
        public double DiagnosticsSeconds { get; set; }
        #endregion
    }
}
