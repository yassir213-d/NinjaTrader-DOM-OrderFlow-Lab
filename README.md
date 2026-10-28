# NQ DOM Order Flow — Experimental NinjaTrader 8 Framework

> **SIMULATION ONLY — DO NOT USE WITH REAL MONEY.**
>
> This project is an educational, experimental foundation for building and studying an order-flow system. It is **not a finished trading system**, a live-trading recommendation, or a proven profitable strategy. Run it only on a simulation account.

## Why this project exists

I am sharing this code so other developers and order-flow enthusiasts can learn from it, inspect its logic, and use it as a starting point for their own experiments.

The goal is to bring several DOM concepts together in one system and observe how they interact. The goal is **not** to provide a bot that you can download and trade with real money.

**Main source file:** [`NQDomOrderFlowProBiasFilteredV4.cs`](NQDomOrderFlowProBiasFilteredV4.cs)

**Platform:** NinjaTrader 8 / NinjaScript (C#).

## Important: frequent entries and experimental signals

This algo is highly reactive to short-term market activity. During testing, it can produce many entry signals or attempts around price moves and changing visible liquidity, including large displayed orders. Frequent activity does not mean high-quality opportunities.

The current V4 code does contain confirmation rules, filters, cooldowns, and a daily trade limit. It does **not literally enter on every move or every large order**. Nevertheless, its selectivity and execution behavior need careful investigation in simulation.

**Use it to build and understand a system—not as a ready-to-trade system.**

## DOM concepts explored

| Concept | What the code attempts to measure |
| --- | --- |
| Order book imbalance | Relative displayed bid/ask liquidity across selected depth levels. |
| Order-flow imbalance (OFI) | Directional pressure estimated from changes in the order book. |
| Absorption | Aggressive buying or selling accompanied by limited price movement. |
| Pulling / stacking | Removal or addition of displayed liquidity near the best bid and ask. |
| Iceberg detection — heuristic | Possible hidden liquidity inferred from executed volume and visible replenishment. |
| Spoofing filter — heuristic | Suspicious large displayed liquidity that disappears quickly with relatively little execution. |
| Aggressor delta and price response | Estimated buying/selling aggression checked against the recent price reaction. |

These are experimental interpretations of market data. **Iceberg and spoofing flags are estimates, not confirmed detections.** The code cannot establish a participant's identity or intent. A large visible order is not, by itself, a reliable trading signal.

## How V4 combines the signals

- **Continuation:** requires alignment in at least three of four readings: OFI, aggressor delta, price response, and book imbalance, with additional contradiction checks.
- **Absorption reversal:** requires absorption, supporting OFI, contained price movement, and a supporting DOM clue.
- **Filters:** include suspected-spoof vetoes, spread checks, a range filter, signal persistence, a no-chase rule, and directional-flip confirmation.
- **Trend context:** a 5-minute EMA filter is enabled by default for continuation entries; reversals bypass it by default.
- **Entry orders:** continuation uses marketable limit orders; absorption reversals use passive limit orders. Unfilled entries have timeouts.
- **Re-entry controls:** include warmup, cooldown, signal re-arming, and a configurable daily trade cap.

## Current default settings

These values describe the supplied source code. **They are not recommended trading settings.**

| Setting | Default |
| --- | --- |
| Contracts | 1 |
| Profit target | 300 in currency mode |
| Stop loss | 500 in currency mode |
| Maximum trades per day | 10 |
| DOM warmup | 10 seconds |
| Entry cooldown | 30 seconds |
| Signal confirmation | 1.5 seconds |
| Higher-timeframe EMA | 20-period EMA on 5-minute bars |

The source labels the currency offsets in dollars and configures them for the strategy position. These are not tick distances or guarantees of realized profit/loss. The exit model uses a fixed stop/target bracket, with no breakeven, trailing stop, time exit, or discretionary DOM exit. Session-close flattening is disabled in the supplied defaults.

## Simulation setup

1. Add the supplied strategy source to your NinjaTrader 8 NinjaScript environment and compile it. Resolve any compilation errors before enabling it.
2. Provide streaming Level-II market depth and trade data for the instrument being studied.
3. Select a **simulation account**, such as Sim101, before enabling the strategy. Confirm the account selection every time.
4. Review the parameters and start with the supplied one-contract setting. The source suggests a 1-minute primary chart and adds a 5-minute series internally.
5. Keep diagnostics enabled. Inspect the NinjaScript Output messages for DOM readiness, rejected signals, entry requests, fills, and cancellations.

**SIM-only is the intended usage policy, not an account lock implemented by this source.** Do not assume the code prevents selection of a live account.

## Testing limitations

- This is not a conventional historical Strategy Analyzer strategy: it relies on streaming depth events and real-time processing.
- Ordinary candle data is insufficient to reproduce its DOM behavior.
- Playback requires appropriate recorded depth/trade data and separate validation. The code uses `DateTime.UtcNow` for timing, so changing playback speed can distort time-window behavior.
- Simulation fills do not demonstrate real-world execution quality. Queue position, slippage, commissions, latency, and incomplete depth data can materially change results.
- No verified profitability, live-readiness, or compilation guarantee is claimed by this README.

## Contributing

Useful contributions include clearer diagnostics, reproducible simulation tests, better signal selectivity, more accurate event handling, and documented limitations of the detection heuristics.

When reporting an issue, include the NinjaTrader version, data feed, instrument, chart settings, strategy parameters, and relevant logs. Remove account identifiers and other private information.

**Learn from it, test it in SIM, and improve it. Do not deploy it with real money.**
