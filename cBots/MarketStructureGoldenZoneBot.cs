// =====================================================================
//  Market Structure Golden Zone Bot
//  Platform: cTrader Automate (cAlgo API, C#)
//
//  STEP 1 - Market structure
//   - Swing highs / lows are fractal pivots (N bars lower/higher on each side).
//   - BULLISH break: price breaks above the latest swing high.
//        -> lowest low before the break = HL, peak after the break = HH
//   - BEARISH break: price breaks below the latest swing low.
//        -> highest high before the break = LH, trough after the break = LL
//   - Break WITH the trend = BOS, AGAINST the trend = CHoCH.
//
//  STEP 2 - Fibonacci golden zone
//   - HH confirmed -> Fib from HL (level 1) to HH (level 0).
//   - LL confirmed -> Fib from LH (level 1) to LL (level 0).
//   - Levels: 0, 0.62, 0.705, 0.79, 1, -0.27, -0.62, -2
//   - Golden zone = 0.62 -> 0.79.
//
//  STEP 3 - Trade execution
//   - Trend HH/HL (bullish): BUY LIMIT at the 0.62 level.
//   - Trend LH/LL (bearish): SELL LIMIT at the 0.62 level.
//   - Stop loss at Fib level 1 (the HL for buys, the LH for sells).
//   - Take profit = Risk:Reward x stop distance (default 1:2).
//   - The pending order is cancelled if the setup is invalidated,
//     replaced by a newer Fib, or the trend flips.
//
//  STEP 4 - Open-trade management (partial profit + break even)
//   - 1R is stored from the ORIGINAL entry and ORIGINAL stop when the
//     position opens, and is never recalculated.
//   - At +1R (Bid for buys, Ask for sells), ONCE per position:
//       * close 50% of the ORIGINAL volume (normalized to broker steps),
//       * move the stop on the remaining volume to the original entry,
//       * keep the original 2R take profit.
//   - The runner then exits at the 2R take profit or at break even.
//
//  NEWS FILTER (live trading only)
//   - Downloads the Forex Factory weekly calendar (JSON) every few hours.
//   - Around high-impact events for the chosen currencies it cancels
//     pending orders and places no new ones; optionally closes open trades.
//   - When the news window ends, a still-valid setup gets its order again.
//   - Needs FULL ACCESS (internet). Not available in backtests (the feed
//     only contains the current week).
//   - DAILY NEWS WINDOW (backtest + live): every weekday around 08:30
//     New York time (US data releases), with automatic US daylight saving.
//
//  MONTHLY PROFIT TARGET / MONTHLY TRADING LOCK
//   - At the start of each calendar month (server/backtest time) the
//     account BALANCE is recorded; target = start x (1 + target %).
//   - Compounds: every month starts from the actual balance at that time.
//   - When the realized BALANCE reaches the target, no new trades are
//     opened for the rest of the month (pending orders are cancelled).
//     Open positions keep their normal SL / 1R partial / BE / 2R management.
//   - Survives restarts: state is saved (live) and can be rebuilt from
//     this month's closed-trade history.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum BreakConfirmation
    {
        CandleClose,
        Wick
    }

    public enum StructureTrend
    {
        None,
        Bullish,
        Bearish
    }

    public enum NewsImpactFilter
    {
        HighOnly,
        MediumAndHigh
    }

    public class NewsEvent
    {
        public DateTime TimeUtc;
        public string Title;
        public string Currency;
        public string Impact;
    }

    public enum VolumeSizing
    {
        FixedLots,
        RiskPercentOfBalance
    }

    public class SwingPoint
    {
        public int Index;
        public double Price;
        public DateTime Time;
        public string Label = "";
        public bool Broken;

        public SwingPoint(int index, double price, DateTime time)
        {
            Index = index;
            Price = price;
            Time = time;
        }
    }

    public class FibSetup
    {
        public bool IsBullish;
        public SwingPoint One;      // level 1  (HL for bullish, LH for bearish)
        public SwingPoint Zero;     // level 0  (HH for bullish, LL for bearish)
        public double ZoneHigh;
        public double ZoneLow;
        public double MidPrice;
        public bool Touched;
        public bool Invalidated;
        public int TouchIndex = -1;

        public bool TradeAttempted;
        public PendingOrder Order;

        // price = level0 + level * (level1 - level0)
        public double PriceAt(double level) => Zero.Price + level * (One.Price - Zero.Price);
    }

    // Per-position trade-management state (STEP 4). Filled once when the position
    // opens and never recalculated afterwards.
    public class PositionState
    {
        public int PositionId;
        public TradeType Direction;
        public double OriginalEntry;
        public double OriginalStopLoss;
        public double OriginalRisk;        // price distance entry -> original SL
        public double OriginalVolume;      // units, BEFORE any partial close
        public double OneRPrice;
        public double TwoRPrice;
        public double? FinalTakeProfit;    // the take profit the position was opened with
        public bool OneRReached;
        public bool PartialTaken;          // true once the 50% close has been ATTEMPTED (never retried)
        public bool BreakEvenActivated;    // true once the stop is at the original entry
        public int BreakEvenAttempts;
    }

    [Robot(AccessRights = AccessRights.FullAccess, AddIndicators = true)]
    public class MarketStructureBot : Robot
    {
        // ---------------- Structure ----------------
        [Parameter("Swing Strength (bars each side)", DefaultValue = 5, MinValue = 1, Group = "Structure")]
        public int SwingStrength { get; set; }

        [Parameter("Break Confirmation", DefaultValue = BreakConfirmation.CandleClose, Group = "Structure")]
        public BreakConfirmation BreakMode { get; set; }

        // ---------------- Fibonacci ----------------
        [Parameter("Fib Levels", DefaultValue = "0,0.62,0.705,0.79,1,-0.27,-0.62,-2", Group = "Fibonacci")]
        public string FibLevelsText { get; set; }

        [Parameter("Golden Zone Start (entry)", DefaultValue = 0.62, Group = "Fibonacci")]
        public double GoldenStart { get; set; }

        [Parameter("Golden Zone Mid", DefaultValue = 0.705, Group = "Fibonacci")]
        public double GoldenMid { get; set; }

        [Parameter("Golden Zone End", DefaultValue = 0.79, Group = "Fibonacci")]
        public double GoldenEnd { get; set; }

        [Parameter("Stop Loss Fib Level", DefaultValue = 1.0, Group = "Fibonacci")]
        public double StopLevel { get; set; }

        [Parameter("Fib Extend (bars)", DefaultValue = 60, MinValue = 5, Group = "Fibonacci")]
        public int FibExtendBars { get; set; }

        [Parameter("Show Only Latest Fib", DefaultValue = true, Group = "Fibonacci")]
        public bool ShowOnlyLatestFib { get; set; }

        // ---------------- Trading ----------------
        [Parameter("Enable Trading", DefaultValue = true, Group = "Trading")]
        public bool EnableTrading { get; set; }

        [Parameter("Risk:Reward (1:X)", DefaultValue = 2.0, MinValue = 0.1, Group = "Trading")]
        public double RewardRatio { get; set; }

        [Parameter("Volume Sizing", DefaultValue = VolumeSizing.RiskPercentOfBalance, Group = "Trading")]
        public VolumeSizing SizingMode { get; set; }

        [Parameter("Fixed Lots", DefaultValue = 0.01, MinValue = 0.0001, Group = "Trading")]
        public double FixedLots { get; set; }

        [Parameter("Risk % of Balance", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 100, Group = "Trading")]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss Buffer (pips)", DefaultValue = 0, MinValue = 0, Group = "Trading")]
        public double StopBufferPips { get; set; }

        [Parameter("Min Stop Distance (price points, 0 = off)", DefaultValue = 40, MinValue = 0, Group = "Trading")]
        public double MinStopDistance { get; set; }

        [Parameter("Max Open Trades", DefaultValue = 1, MinValue = 1, Group = "Trading")]
        public int MaxOpenTrades { get; set; }

        [Parameter("Trade Label", DefaultValue = "MSB_GoldenZone", Group = "Trading")]
        public string TradeLabel { get; set; }

        [Parameter("Cancel Orders On Stop", DefaultValue = true, Group = "Trading")]
        public bool CancelOrdersOnStop { get; set; }

        [Parameter("Close Positions Without Stop Loss", DefaultValue = true, Group = "Trading")]
        public bool CloseUnprotectedPositions { get; set; }

        // ---------------- Monthly target ----------------
        [Parameter("Monthly Profit Target %", DefaultValue = 10.0, MinValue = 0.1, Group = "Monthly Target")]
        public double MonthlyProfitTargetPercent { get; set; }

        // ---------------- News filter ----------------
        [Parameter("Enable News Filter", DefaultValue = true, Group = "News")]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Currencies", DefaultValue = "USD", Group = "News")]
        public string NewsCurrencies { get; set; }

        [Parameter("News Impact", DefaultValue = NewsImpactFilter.HighOnly, Group = "News")]
        public NewsImpactFilter NewsImpact { get; set; }

        [Parameter("Minutes Before News", DefaultValue = 30, MinValue = 0, Group = "News")]
        public int MinutesBeforeNews { get; set; }

        [Parameter("Minutes After News", DefaultValue = 30, MinValue = 0, Group = "News")]
        public int MinutesAfterNews { get; set; }

        [Parameter("Close Open Trades Before News", DefaultValue = false, Group = "News")]
        public bool CloseTradesBeforeNews { get; set; }

        [Parameter("Calendar Refresh (hours)", DefaultValue = 4, MinValue = 1, Group = "News")]
        public int NewsRefreshHours { get; set; }

        [Parameter("Calendar URL", DefaultValue = "https://nfs.faireconomy.media/ff_calendar_thisweek.json", Group = "News")]
        public string NewsCalendarUrl { get; set; }

        [Parameter("Enable Daily News Window", DefaultValue = true, Group = "News")]
        public bool EnableDailyNewsWindow { get; set; }

        [Parameter("Daily News Times (New York)", DefaultValue = "08:30", Group = "News")]
        public string DailyNewsTimesText { get; set; }

        // ---------------- Display ----------------
        [Parameter("Draw on Chart", DefaultValue = true, Group = "Display")]
        public bool DrawOnChart { get; set; }

        [Parameter("Print to Log", DefaultValue = true, Group = "Display")]
        public bool PrintToLog { get; set; }

        // ---------------- Public state ----------------
        public StructureTrend Trend { get; private set; } = StructureTrend.None;
        public SwingPoint LastHH { get; private set; }
        public SwingPoint LastHL { get; private set; }
        public SwingPoint LastLH { get; private set; }
        public SwingPoint LastLL { get; private set; }
        public FibSetup ActiveBullFib { get; private set; }
        public FibSetup ActiveBearFib { get; private set; }

        // ---------------- Internal state ----------------
        private SwingPoint _swingHigh;
        private SwingPoint _swingLow;

        private bool _pendingHH;
        private double _brokenHighLevel;
        private int _hhSearchFrom;

        private bool _pendingLL;
        private double _brokenLowLevel;
        private int _llSearchFrom;

        private bool _loadingHistory;
        private readonly Dictionary<string, string> _labels = new Dictionary<string, string>();
        private readonly List<double> _fibLevels = new List<double>();

        // STEP 4: trade-management state, keyed by Position.Id
        private readonly Dictionary<int, PositionState> _positionStates = new Dictionary<int, PositionState>();

        private bool CanDraw => DrawOnChart && RunningMode != RunningMode.Optimization;

        // =====================================================================
        protected override void OnStart()
        {
            ParseFibLevels();

            PendingOrders.Filled += OnOrderFilled;
            Positions.Closed += OnPositionClosed;
            Positions.Opened += OnPositionOpened;

            // STEP 4: pick up positions this bot already had open (e.g. after a restart).
            foreach (var p in Positions.Where(IsOwnPosition).ToList())
                RegisterPosition(p, true);

            // Remove leftover pending orders from a previous run (they are re-created below).
            foreach (var o in PendingOrders.Where(o => o.Label == TradeLabel && o.SymbolName == SymbolName).ToList())
                CancelPendingOrder(o);

            _loadingHistory = true;
            for (int i = 0; i < Bars.Count - 1; i++)
                ProcessBar(i);
            _loadingHistory = false;

            Print("Bot started. Trend: {0}. Fib levels: {1}. Trading: {2}",
                Trend, string.Join(", ", _fibLevels), EnableTrading ? "ON" : "OFF");

            // Monthly target: restore or start this month's state BEFORE any order can be placed.
            InitMonthlyTarget();

            // If the latest setup is still waiting for price, place its order now.
            if (Trend == StructureTrend.Bullish) TryPlaceOrder(ActiveBullFib);
            if (Trend == StructureTrend.Bearish) TryPlaceOrder(ActiveBearFib);

            InitNewsFilter();
        }

        protected override void OnTimer()
        {
            if (!NewsFilterOn) return;
            if (_newsActive && !_fetchInProgress && DateTime.UtcNow >= _nextFetchUtc) FetchCalendar();
            UpdateNewsState();
        }

        protected override void OnBar()
        {
            CheckMonthlyTarget();         // month change / target check before new signals
            ProcessBar(Bars.Count - 2);   // the bar that just closed
        }

        protected override void OnTick()
        {
            _tickCount++;
            CheckMonthlyTarget();
            if (NewsFilterOn) UpdateNewsState();
            ManageOpenPositions();        // STEP 4: partial close + break even
        }

        protected override void OnStop()
        {
            if (!CancelOrdersOnStop) return;
            foreach (var o in PendingOrders.Where(o => o.Label == TradeLabel && o.SymbolName == SymbolName).ToList())
                CancelPendingOrder(o);
        }

        private void ParseFibLevels()
        {
            _fibLevels.Clear();
            foreach (var part in (FibLevelsText ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    _fibLevels.Add(v);
            }

            if (_fibLevels.Count == 0)
            {
                Print("Could not read 'Fib Levels' - using defaults.");
                _fibLevels.AddRange(new[] { 0, 0.62, 0.705, 0.79, 1, -0.27, -0.62, -2 });
            }
        }

        // =====================================================================
        //  Core processing
        // =====================================================================
        private void ProcessBar(int i)
        {
            int p = i - SwingStrength;
            if (p - SwingStrength >= 0)
            {
                if (IsPivotHigh(p)) OnPivotHigh(p, i);
                if (IsPivotLow(p)) OnPivotLow(p, i);
            }

            double upPrice = BreakMode == BreakConfirmation.CandleClose ? Bars.ClosePrices[i] : Bars.HighPrices[i];
            double downPrice = BreakMode == BreakConfirmation.CandleClose ? Bars.ClosePrices[i] : Bars.LowPrices[i];

            if (_swingHigh != null && !_swingHigh.Broken && upPrice > _swingHigh.Price)
                BullishBreak(i);

            if (_swingLow != null && !_swingLow.Broken && downPrice < _swingLow.Price)
                BearishBreak(i);

            CheckFib(ActiveBullFib, i);
            CheckFib(ActiveBearFib, i);
        }

        private bool IsPivotHigh(int p)
        {
            double h = Bars.HighPrices[p];
            for (int k = 1; k <= SwingStrength; k++)
            {
                if (Bars.HighPrices[p - k] >= h) return false;
                if (Bars.HighPrices[p + k] > h) return false;
            }
            return true;
        }

        private bool IsPivotLow(int p)
        {
            double l = Bars.LowPrices[p];
            for (int k = 1; k <= SwingStrength; k++)
            {
                if (Bars.LowPrices[p - k] <= l) return false;
                if (Bars.LowPrices[p + k] < l) return false;
            }
            return true;
        }

        private void OnPivotHigh(int p, int currentIndex)
        {
            if (_swingHigh != null && p <= _swingHigh.Index) return;

            if (_pendingHH && p >= _hhSearchFrom && Bars.HighPrices[p] > _brokenHighLevel)
            {
                ResolvePendingHH(p, currentIndex, true);
                return;
            }

            _swingHigh = new SwingPoint(p, Bars.HighPrices[p], Bars.OpenTimes[p]);
        }

        private void OnPivotLow(int p, int currentIndex)
        {
            if (_swingLow != null && p <= _swingLow.Index) return;

            if (_pendingLL && p >= _llSearchFrom && Bars.LowPrices[p] < _brokenLowLevel)
            {
                ResolvePendingLL(p, currentIndex, true);
                return;
            }

            _swingLow = new SwingPoint(p, Bars.LowPrices[p], Bars.OpenTimes[p]);
        }

        private void BullishBreak(int i)
        {
            var broken = _swingHigh;
            broken.Broken = true;
            bool isChoch = Trend == StructureTrend.Bearish;

            if (_pendingLL) ResolvePendingLL(i, i, false);

            int lowIdx = LowestIndex(broken.Index + 1, i);
            var hl = new SwingPoint(lowIdx, Bars.LowPrices[lowIdx], Bars.OpenTimes[lowIdx]);
            Mark(hl, "HL", false);
            LastHL = hl;
            _swingLow = hl;

            Trend = StructureTrend.Bullish;
            DrawBreak(broken, i, isChoch ? "CHoCH" : "BOS", true);

            // Trend is now bullish -> any waiting SELL order is no longer valid.
            CancelFibOrder(ActiveBearFib, "trend turned bullish");

            _pendingHH = true;
            _brokenHighLevel = broken.Price;
            _hhSearchFrom = broken.Index + 1;
        }

        private void BearishBreak(int i)
        {
            var broken = _swingLow;
            broken.Broken = true;
            bool isChoch = Trend == StructureTrend.Bullish;

            if (_pendingHH) ResolvePendingHH(i, i, false);

            int highIdx = HighestIndex(broken.Index + 1, i);
            var lh = new SwingPoint(highIdx, Bars.HighPrices[highIdx], Bars.OpenTimes[highIdx]);
            Mark(lh, "LH", true);
            LastLH = lh;
            _swingHigh = lh;

            Trend = StructureTrend.Bearish;
            DrawBreak(broken, i, isChoch ? "CHoCH" : "BOS", false);

            // Trend is now bearish -> any waiting BUY order is no longer valid.
            CancelFibOrder(ActiveBullFib, "trend turned bearish");

            _pendingLL = true;
            _brokenLowLevel = broken.Price;
            _llSearchFrom = broken.Index + 1;
        }

        private void ResolvePendingHH(int upTo, int currentIndex, bool createFib)
        {
            int idx = HighestIndex(_hhSearchFrom, upTo);
            var hh = new SwingPoint(idx, Bars.HighPrices[idx], Bars.OpenTimes[idx]);
            Mark(hh, "HH", true);
            LastHH = hh;
            if (_swingHigh == null || _swingHigh.Broken || idx > _swingHigh.Index)
                _swingHigh = hh;
            _pendingHH = false;

            if (createFib) CreateFib(true, LastHL, hh, currentIndex);
        }

        private void ResolvePendingLL(int upTo, int currentIndex, bool createFib)
        {
            int idx = LowestIndex(_llSearchFrom, upTo);
            var ll = new SwingPoint(idx, Bars.LowPrices[idx], Bars.OpenTimes[idx]);
            Mark(ll, "LL", false);
            LastLL = ll;
            if (_swingLow == null || _swingLow.Broken || idx > _swingLow.Index)
                _swingLow = ll;
            _pendingLL = false;

            if (createFib) CreateFib(false, LastLH, ll, currentIndex);
        }

        // =====================================================================
        //  STEP 2 - Fibonacci golden zone
        // =====================================================================
        private void CreateFib(bool bullish, SwingPoint one, SwingPoint zero, int currentIndex)
        {
            if (one == null || zero == null || one.Index >= zero.Index) return;

            var f = new FibSetup { IsBullish = bullish, One = one, Zero = zero };
            double a = f.PriceAt(GoldenStart);
            double b = f.PriceAt(GoldenEnd);
            f.ZoneHigh = Math.Max(a, b);
            f.ZoneLow = Math.Min(a, b);
            f.MidPrice = f.PriceAt(GoldenMid);

            // A newer setup replaces the old one -> cancel the old unfilled order.
            if (bullish)
            {
                CancelFibOrder(ActiveBullFib, "replaced by a newer bullish Fib");
                ActiveBullFib = f;
            }
            else
            {
                CancelFibOrder(ActiveBearFib, "replaced by a newer bearish Fib");
                ActiveBearFib = f;
            }

            if (PrintToLog && !_loadingHistory)
                Print("{0} Fib drawn: 1 = {1}, 0 = {2}. Golden zone {3} - {4}",
                    bullish ? "Bullish" : "Bearish", Fmt(one.Price), Fmt(zero.Price),
                    Fmt(f.ZoneLow), Fmt(f.ZoneHigh));

            DrawFib(f);

            // The Fib is confirmed a few bars after the HH/LL: check those bars too.
            for (int k = zero.Index + 1; k < currentIndex; k++)
                CheckFib(f, k);

            // STEP 3: place the order at 0.62 (live only, not while rebuilding history)
            if (!_loadingHistory) TryPlaceOrder(f);
        }

        private void CheckFib(FibSetup f, int i)
        {
            if (f == null || f.Invalidated || i <= f.Zero.Index) return;

            double lo = Bars.LowPrices[i];
            double hi = Bars.HighPrices[i];

            if (f.IsBullish)
            {
                if (lo < f.One.Price) { Invalidate(f); return; }
                if (!f.Touched && lo <= f.ZoneHigh) Touch(f, i);
            }
            else
            {
                if (hi > f.One.Price) { Invalidate(f); return; }
                if (!f.Touched && hi >= f.ZoneLow) Touch(f, i);
            }
        }

        private void Touch(FibSetup f, int i)
        {
            f.Touched = true;
            f.TouchIndex = i;

            if (PrintToLog && !_loadingHistory)
                Print("Price touched the {0} golden zone at {1:yyyy-MM-dd HH:mm}",
                    f.IsBullish ? "BULLISH" : "BEARISH", Bars.OpenTimes[i]);

            if (CanDraw)
            {
                string name = FibId(f) + "_TOUCH";
                if (f.IsBullish)
                    Chart.DrawIcon(name, ChartIconType.UpArrow, i, Bars.LowPrices[i], Color.LimeGreen);
                else
                    Chart.DrawIcon(name, ChartIconType.DownArrow, i, Bars.HighPrices[i], Color.Red);
            }
        }

        private void Invalidate(FibSetup f)
        {
            f.Invalidated = true;
            if (PrintToLog && !_loadingHistory)
                Print("{0} Fib invalidated: price went beyond level 1 ({1})",
                    f.IsBullish ? "Bullish" : "Bearish", Fmt(f.One.Price));
            CancelFibOrder(f, "setup invalidated");
        }

        // =====================================================================
        //  STEP 3 - Trade execution
        // =====================================================================
        private void TryPlaceOrder(FibSetup f)
        {
            if (!EnableTrading || f == null || f.TradeAttempted || f.Touched || f.Invalidated) return;

            // Only trade in the direction of the structure.
            if (f.IsBullish && Trend != StructureTrend.Bullish) return;
            if (!f.IsBullish && Trend != StructureTrend.Bearish) return;

            // Monthly profit target: no new trades once this month's target is reached.
            if (!CanOpenNewTrade()) return;
            if (f.TradeAttempted) return;   // the monthly reset above may already have placed this setup

            // News filter: hold the setup; it is retried when the news window ends.
            if (NewsFilterOn && IsNewsBlackout(out var newsEvent))
            {
                Print("{0} setup on hold - news window for '{1}' ({2}) at {3:yyyy-MM-dd HH:mm} UTC.",
                    f.IsBullish ? "BUY" : "SELL", newsEvent.Title, newsEvent.Currency, newsEvent.TimeUtc);
                return;
            }

            f.TradeAttempted = true;

            int open = Positions.Count(p => p.Label == TradeLabel && p.SymbolName == SymbolName);
            if (open >= MaxOpenTrades)
            {
                Print("Skipped {0} setup: already {1} open trade(s).", f.IsBullish ? "BUY" : "SELL", open);
                return;
            }

            var type = f.IsBullish ? TradeType.Buy : TradeType.Sell;
            double entry = f.PriceAt(GoldenStart);          // 0.62
            double buffer = StopBufferPips * Symbol.PipSize;
            double stop = f.IsBullish ? f.PriceAt(StopLevel) - buffer   // level 1 = HL
                                      : f.PriceAt(StopLevel) + buffer;  // level 1 = LH

            // If price is already at/through 0.62, enter at market; otherwise wait with a limit order.
            double market = f.IsBullish ? Symbol.Ask : Symbol.Bid;
            bool useMarket = f.IsBullish ? market <= entry : market >= entry;
            if (useMarket) entry = market;

            // Price already beyond the stop -> no trade.
            if ((f.IsBullish && entry <= stop) || (!f.IsBullish && entry >= stop))
            {
                Print("Skipped {0} setup: price is already beyond the stop level.", type);
                return;
            }

            // Minimum stop size: tiny stops sit inside normal market noise and get hit almost at once.
            double stopDistance = Math.Abs(entry - stop);
            if (MinStopDistance > 0 && stopDistance < MinStopDistance)
            {
                Print("Skipped {0} setup: stop distance {1} is below the minimum of {2} (entry {3}, stop {4}).",
                    type, Fmt(stopDistance), Fmt(MinStopDistance), Fmt(entry), Fmt(stop));
                return;
            }

            double slPips = Math.Abs(entry - stop) / Symbol.PipSize;
            double tpPips = slPips * RewardRatio;
            double target = f.IsBullish ? entry + tpPips * Symbol.PipSize : entry - tpPips * Symbol.PipSize;

            double volume = CalculateVolume(slPips);
            if (volume <= 0) return;

            TradeResult result = useMarket
                ? ExecuteMarketOrder(type, SymbolName, volume, TradeLabel, slPips, tpPips)
                : PlaceLimitOrder(type, SymbolName, volume, entry, TradeLabel,
                    Math.Round(stop, Symbol.Digits), Math.Round(target, Symbol.Digits), ProtectionType.Absolute);

            if (!result.IsSuccessful)
            {
                Print("{0} order FAILED: {1}", type, result.Error);
                return;
            }

            if (!useMarket) f.Order = result.PendingOrder;

            Print("{0} {1} placed | Entry {2} | SL {3} ({4:0.0} pips) | TP {5} ({6:0.0} pips) | RR 1:{7} | Volume {8}",
                type, useMarket ? "MARKET" : "LIMIT", Fmt(entry), Fmt(stop), slPips,
                Fmt(target), tpPips, RewardRatio, volume);

            if (CanDraw) DrawTradeBox(f, entry, stop, target);
        }

        private double CalculateVolume(double slPips)
        {
            double units;
            if (SizingMode == VolumeSizing.FixedLots)
            {
                units = Symbol.QuantityToVolumeInUnits(FixedLots);
            }
            else
            {
                double riskMoney = Account.Balance * RiskPercent / 100.0;
                units = riskMoney / (slPips * Symbol.PipValue);
            }

            units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);

            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("Skipped trade: calculated volume is below the broker minimum ({0} units). " +
                      "Increase Risk % or use Fixed Lots.", Symbol.VolumeInUnitsMin);
                return 0;
            }

            return Math.Min(units, Symbol.VolumeInUnitsMax);
        }

        private void CancelFibOrder(FibSetup f, string reason)
        {
            if (f == null || f.Order == null) return;

            var order = PendingOrders.FirstOrDefault(o => o.Id == f.Order.Id);
            if (order != null)
            {
                CancelPendingOrder(order);
                Print("Cancelled pending {0} order: {1}", order.TradeType, reason);
            }
            f.Order = null;
        }

        private void OnOrderFilled(PendingOrderFilledEventArgs args)
        {
            if (args.Position.Label != TradeLabel) return;
            Print("Order FILLED: {0} at {1} | SL {2} | TP {3}", args.Position.TradeType,
                Fmt(args.Position.EntryPrice), Fmt(args.Position.StopLoss ?? 0), Fmt(args.Position.TakeProfit ?? 0));
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (!IsOwnPosition(args.Position)) return;
            Print("Position CLOSED ({0}): {1} net profit {2:0.00}", args.Reason,
                args.Position.TradeType, args.Position.NetProfit);

            LogFinalExit(args.Position, args.Reason);
            CheckMonthlyTarget();
            _noStopWarned.Remove(args.Position.Id);
            _noStopSinceTick.Remove(args.Position.Id);
        }

        // =====================================================================
        //  STEP 4 - Open-trade management: 50% at +1R, runner to break even,
        //  final exit at the original 2R take profit or at break even.
        // =====================================================================
        private readonly HashSet<int> _noStopWarned = new HashSet<int>();
        private readonly Dictionary<int, long> _noStopSinceTick = new Dictionary<int, long>();
        private long _tickCount;

        private bool IsOwnPosition(Position p) =>
            p != null && p.Label == TradeLabel && p.SymbolName == SymbolName;

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (!IsOwnPosition(args.Position)) return;
            RegisterPosition(args.Position, false);
        }

        // Stores the ORIGINAL entry, stop, risk and volume. Called once per position.
        // Returns false if the position cannot be managed yet (no stop loss attached).
        private bool RegisterPosition(Position p, bool afterRestart)
        {
            if (_positionStates.ContainsKey(p.Id)) return true;

            if (!p.StopLoss.HasValue)
            {
                if (_noStopWarned.Add(p.Id))
                {
                    _noStopSinceTick[p.Id] = _tickCount;
                    Print("WARNING: position {0} has NO stop loss (e.g. filled through the stop on a price gap). {1}",
                        p.Id, CloseUnprotectedPositions
                            ? "It will be closed if no stop loss is attached by the next tick."
                            : "'Close Positions Without Stop Loss' is OFF - the position is UNPROTECTED.");
                }
                return false;
            }

            bool isBuy = p.TradeType == TradeType.Buy;
            double entry = p.EntryPrice;
            double sl = p.StopLoss.Value;
            double risk = isBuy ? entry - sl : sl - entry;

            var s = new PositionState
            {
                PositionId = p.Id,
                Direction = p.TradeType,
                OriginalEntry = entry,
                OriginalStopLoss = sl,
                OriginalRisk = risk,
                OriginalVolume = p.VolumeInUnits,
                OneRPrice = isBuy ? entry + risk : entry - risk,
                TwoRPrice = isBuy ? entry + 2 * risk : entry - 2 * risk,
                FinalTakeProfit = p.TakeProfit
            };

            // A stop at (or beyond) the entry means there is no initial risk to measure 1R from.
            // After a restart this normally means break even was already applied earlier.
            if (risk <= Symbol.TickSize / 2)
            {
                s.OneRReached = true;
                s.PartialTaken = true;
                s.BreakEvenActivated = true;
                _positionStates[p.Id] = s;
                Print("Position {0}: stop is already at/through the entry ({1}) - treated as a break-even runner, " +
                      "no further partial close.", p.Id, Fmt(sl));
                return true;
            }

            _positionStates[p.Id] = s;

            Print("---------------- TRADE OPENED{0} ----------------", afterRestart ? " (picked up after restart)" : "");
            Print("Position ID: {0}", s.PositionId);
            Print("Direction: {0}", s.Direction);
            Print("Entry: {0}", Fmt(s.OriginalEntry));
            Print("Original SL: {0}", Fmt(s.OriginalStopLoss));
            Print("Original Risk (1R distance): {0}", Fmt(s.OriginalRisk));
            Print("Original Volume: {0}", s.OriginalVolume);
            Print("1R Level: {0}", Fmt(s.OneRPrice));
            Print("2R Level: {0}", Fmt(s.TwoRPrice));
            Print("Take Profit on position: {0}", s.FinalTakeProfit.HasValue ? Fmt(s.FinalTakeProfit.Value) : "none");
            return true;
        }

        private void ManageOpenPositions()
        {
            foreach (var p in Positions.Where(IsOwnPosition).ToList())
            {
                if (!_positionStates.TryGetValue(p.Id, out var s))
                {
                    // e.g. the stop loss was attached after the Opened event
                    if (!RegisterPosition(p, false))
                    {
                        CloseIfStillUnprotected(p);
                        continue;
                    }
                    s = _positionStates[p.Id];
                }

                if (s.PartialTaken && s.BreakEvenActivated) continue;   // nothing left to do

                bool isBuy = s.Direction == TradeType.Buy;
                double price = isBuy ? Symbol.Bid : Symbol.Ask;

                if (!s.OneRReached)
                {
                    bool reached = isBuy ? price >= s.OneRPrice : price <= s.OneRPrice;
                    if (!reached) continue;

                    s.OneRReached = true;
                    TakePartialAndBreakEven(p, s, price);
                }
                else if (!s.BreakEvenActivated)
                {
                    // Only reached if the first break-even modification failed.
                    MoveToBreakEven(p, s);
                }
            }
        }

        private void TakePartialAndBreakEven(Position p, PositionState s, double price)
        {
            // 50% of the ORIGINAL volume, normalized to the broker's volume step.
            double closeVolume = Symbol.NormalizeVolumeInUnits(s.OriginalVolume / 2.0, RoundingMode.ToNearest);
            double currentVolume = p.VolumeInUnits;
            double remaining = Math.Round(currentVolume - closeVolume, 8);

            bool canSplit = closeVolume >= Symbol.VolumeInUnitsMin
                            && closeVolume < currentVolume
                            && remaining >= Symbol.VolumeInUnitsMin - 1e-9;

            // Mark as done BEFORE sending, so the partial close is never repeated on later ticks.
            s.PartialTaken = true;

            Print("---------------- 1R TARGET REACHED ----------------");
            Print("Position ID: {0}", s.PositionId);
            Print("Price: {0}", Fmt(price));

            if (canSplit)
            {
                Print("Closing 50%: {0} of original {1}", closeVolume, s.OriginalVolume);
                Print("Remaining Volume: {0}", remaining);

                var result = ClosePosition(p, closeVolume);
                if (!result.IsSuccessful)
                    Print("Partial close FAILED: {0} (it will not be retried)", result.Error);
            }
            else
            {
                Print("Closing 50%: SKIPPED - volume {0} cannot be split into two valid sizes " +
                      "(min {1}, step {2}). Full position stays open.",
                      currentVolume, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsStep);
                Print("Remaining Volume: {0}", currentVolume);
            }

            Print("Moving SL to Break Even: {0}", Fmt(s.OriginalEntry));
            Print("Final TP: {0}", s.FinalTakeProfit.HasValue ? Fmt(s.FinalTakeProfit.Value) : Fmt(s.TwoRPrice));

            MoveToBreakEven(p, s);
        }

        private void MoveToBreakEven(Position p, PositionState s)
        {
            if (s.BreakEvenActivated || s.BreakEvenAttempts >= 3) return;
            s.BreakEvenAttempts++;

            bool isBuy = s.Direction == TradeType.Buy;
            double be = Math.Round(s.OriginalEntry, Symbol.Digits);

            // If price has already fallen back through the entry, a break-even stop would be
            // on the wrong side of the market - close the runner instead (same outcome as BE).
            bool invalid = isBuy ? Symbol.Bid <= be : Symbol.Ask >= be;
            if (invalid)
            {
                s.BreakEvenActivated = true;
                Print("Price is already back at/through break even ({0}) - closing the remaining position.", Fmt(be));
                var closeResult = ClosePosition(p);
                if (!closeResult.IsSuccessful)
                    Print("Closing remaining position FAILED: {0}", closeResult.Error);
                return;
            }

            var result = p.ModifyStopLossPrice(be);
            if (result.IsSuccessful)
            {
                s.BreakEvenActivated = true;
                Print("Position {0}: SL moved to break even at {1}. TP stays at {2}.", s.PositionId, Fmt(be),
                    p.TakeProfit.HasValue ? Fmt(p.TakeProfit.Value) : "none");

                // The final target must remain the original 2R take profit.
                if (!p.TakeProfit.HasValue)
                {
                    double tp = s.FinalTakeProfit ?? s.TwoRPrice;
                    var tpResult = p.ModifyTakeProfitPrice(Math.Round(tp, Symbol.Digits));
                    if (!tpResult.IsSuccessful)
                        Print("Restoring TP FAILED: {0}", tpResult.Error);
                }
            }
            else
            {
                Print("Break-even modification FAILED (attempt {0}/3): {1}", s.BreakEvenAttempts, result.Error);
            }
        }

        private void LogFinalExit(Position p, PositionCloseReason reason)
        {
            if (!_positionStates.TryGetValue(p.Id, out var s)) return;

            var deals = History.Where(h => h.PositionId == p.Id).OrderBy(h => h.ClosingTime).ToList();
            double finalPrice = deals.Count > 0 ? deals.Last().ClosingPrice : p.CurrentPrice;
            double totalNet = deals.Sum(h => h.NetProfit);

            string exitReason;
            if (reason == PositionCloseReason.TakeProfit)
                exitReason = "2R Take Profit";
            else if (reason == PositionCloseReason.StopLoss)
                exitReason = s.BreakEvenActivated ? "Break Even" : "Original Stop Loss (before 1R)";
            else
                exitReason = "Other (" + reason + ")";

            Print("---------------- {0} ----------------", s.PartialTaken ? "RUNNER CLOSED" : "TRADE CLOSED");
            Print("Position ID: {0}", s.PositionId);
            Print("Exit Reason: {0}", exitReason);
            Print("Final Price: {0}", Fmt(finalPrice));
            Print("Total trade net profit (partial + runner): {0:0.00}", totalNet);

            _positionStates.Remove(p.Id);
            _noStopWarned.Remove(p.Id);
            _noStopSinceTick.Remove(p.Id);
        }

        // SAFETY: a position of this bot must never stay open without a stop loss.
        // This happens when a limit order is filled beyond its own stop (price gaps through
        // both levels, e.g. on news). Such a fill has already broken the setup's level 1,
        // so the setup is invalid and the position is closed at market.
        private void CloseIfStillUnprotected(Position p)
        {
            if (!CloseUnprotectedPositions || p.StopLoss.HasValue) return;
            if (!_noStopSinceTick.TryGetValue(p.Id, out var since) || _tickCount <= since) return;

            _noStopSinceTick.Remove(p.Id);
            Print("SAFETY CLOSE: position {0} ({1} {2} at {3}) has no stop loss - closing at market.",
                p.Id, p.TradeType, p.VolumeInUnits, Fmt(p.EntryPrice));

            var result = ClosePosition(p);
            if (!result.IsSuccessful)
            {
                Print("SAFETY CLOSE FAILED: {0} - will retry on the next tick.", result.Error);
                _noStopSinceTick[p.Id] = _tickCount;
            }
        }

        // Red (risk) and green (reward) boxes like the TradingView position tool
        private void DrawTradeBox(FibSetup f, double entry, double stop, double target)
        {
            string id = FibId(f) + "_TRADE";
            int start = Bars.Count - 1;
            int end = start + FibExtendBars / 2;

            var risk = Chart.DrawRectangle(id + "_SL", start, entry, end, stop, Color.FromArgb(50, 230, 80, 80));
            risk.IsFilled = true;
            var reward = Chart.DrawRectangle(id + "_TP", start, entry, end, target, Color.FromArgb(50, 40, 180, 140));
            reward.IsFilled = true;
        }

        // =====================================================================
        //  NEWS FILTER - live economic calendar (Forex Factory weekly JSON)
        // =====================================================================
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private List<NewsEvent> _newsEvents = new List<NewsEvent>();
        private bool _newsActive;
        private bool _inNewsBlackout;
        private bool _fetchInProgress;
        private DateTime _nextFetchUtc = DateTime.MinValue;

        private string NewsCachePath => Path.Combine(Path.GetTempPath(), "cbot_msb_ff_calendar_thisweek.json");

        private void InitNewsFilter()
        {
            InitDailyNewsWindow();

            if (!EnableNewsFilter)
            {
                Print("News filter: OFF.");
                return;
            }

            if (RunningMode != RunningMode.RealTime)
            {
                Print("News filter: the live calendar only covers the current week, so it is DISABLED in backtests/optimization.");
                return;
            }

            _newsActive = true;

            if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

            // Use the cached copy if it is recent - the feed is rate limited.
            bool usedCache = false;
            try
            {
                if (System.IO.File.Exists(NewsCachePath))
                {
                    var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(NewsCachePath);
                    if (age < TimeSpan.FromHours(NewsRefreshHours))
                    {
                        int n = LoadCalendar(System.IO.File.ReadAllText(NewsCachePath));
                        if (n >= 0)
                        {
                            usedCache = true;
                            _nextFetchUtc = System.IO.File.GetLastWriteTimeUtc(NewsCachePath).AddHours(NewsRefreshHours);
                            Print("News filter: loaded {0} relevant events from cache.", n);
                            PrintUpcomingNews();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Print("News filter: could not read cache ({0}).", ex.Message);
            }

            if (!usedCache) FetchCalendar();

            Timer.Start(TimeSpan.FromSeconds(60));
            UpdateNewsState();
        }

        private void FetchCalendar()
        {
            if (_fetchInProgress) return;
            _fetchInProgress = true;
            _nextFetchUtc = DateTime.UtcNow.AddMinutes(15);   // retry time if this attempt fails
            string url = NewsCalendarUrl;

            Task.Run(async () =>
            {
                try
                {
                    string body = await _http.GetStringAsync(url).ConfigureAwait(false);
                    BeginInvokeOnMainThread(() => OnCalendarDownloaded(body, null));
                }
                catch (Exception ex)
                {
                    BeginInvokeOnMainThread(() => OnCalendarDownloaded(null, ex.Message));
                }
            });
        }

        private void OnCalendarDownloaded(string body, string error)
        {
            _fetchInProgress = false;

            if (error != null)
            {
                Print("News filter: download FAILED ({0}). Keeping {1} known events, retrying in 15 min.", error, _newsEvents.Count);
                return;
            }

            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("["))
            {
                Print("News filter: the calendar site refused the request (rate limit or block). " +
                      "Keeping {0} known events, retrying in 15 min.", _newsEvents.Count);
                return;
            }

            int n = LoadCalendar(body);
            if (n < 0)
            {
                Print("News filter: could not read the calendar data. Retrying in 15 min.");
                return;
            }

            _nextFetchUtc = DateTime.UtcNow.AddHours(NewsRefreshHours);
            try { System.IO.File.WriteAllText(NewsCachePath, body); } catch { /* cache is optional */ }

            Print("News filter: calendar updated - {0} relevant events this week.", n);
            PrintUpcomingNews();
            UpdateNewsState();
        }

        // Returns the number of relevant events, or -1 if the data could not be parsed.
        private int LoadCalendar(string json)
        {
            var currencies = new HashSet<string>(
                (NewsCurrencies ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim().ToUpperInvariant()));

            var list = new List<NewsEvent>();
            var objects = Regex.Matches(json, @"\{[^{}]*\}");
            if (objects.Count == 0) return -1;

            foreach (Match m in objects)
            {
                string obj = m.Value;
                string currency = JsonField(obj, "country").ToUpperInvariant();
                string impact = JsonField(obj, "impact");
                string date = JsonField(obj, "date");

                if (currencies.Count > 0 && !currencies.Contains(currency)) continue;

                bool impactOk = impact.Equals("High", StringComparison.OrdinalIgnoreCase)
                    || (NewsImpact == NewsImpactFilter.MediumAndHigh && impact.Equals("Medium", StringComparison.OrdinalIgnoreCase));
                if (!impactOk) continue;

                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;

                list.Add(new NewsEvent
                {
                    TimeUtc = when.UtcDateTime,
                    Title = JsonField(obj, "title"),
                    Currency = currency,
                    Impact = impact
                });
            }

            _newsEvents = list.OrderBy(e => e.TimeUtc).ToList();
            return _newsEvents.Count;
        }

        private static string JsonField(string obj, string key)
        {
            var m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\/", "/").Replace("\\\"", "\"") : "";
        }

        private void PrintUpcomingNews()
        {
            var now = Server.TimeInUtc;
            foreach (var e in _newsEvents.Where(e => e.TimeUtc >= now.AddMinutes(-MinutesAfterNews)).Take(8))
                Print("   News: {0:ddd dd MMM HH:mm} UTC | {1} | {2} | {3}", e.TimeUtc, e.Currency, e.Impact, e.Title);
        }

        // ---------------- Daily news window (works in backtests too) ----------------
        private bool _dailyWindowActive;
        private readonly List<TimeSpan> _dailyNewsTimes = new List<TimeSpan>();

        private bool NewsFilterOn => _newsActive || _dailyWindowActive;

        private void InitDailyNewsWindow()
        {
            _dailyNewsTimes.Clear();
            if (!EnableDailyNewsWindow)
            {
                Print("Daily news window: OFF.");
                return;
            }

            foreach (var part in (DailyNewsTimesText ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TimeSpan.TryParse(part.Trim(), CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                    _dailyNewsTimes.Add(t);
                else
                    Print("Daily news window: could not read time '{0}' (use HH:mm, e.g. 08:30).", part);
            }

            if (_dailyNewsTimes.Count == 0)
            {
                Print("Daily news window: no valid times - OFF.");
                return;
            }

            _dailyWindowActive = true;
            Print("Daily news window: ON, Mon-Fri at {0} New York time, {1} min before to {2} min after (works in backtests).",
                string.Join(", ", _dailyNewsTimes.Select(t => t.ToString(@"hh\:mm"))), MinutesBeforeNews, MinutesAfterNews);
        }

        private bool IsInDailyNewsWindow(DateTime nowUtc, out NewsEvent ev)
        {
            ev = null;
            DateTime nyToday = UtcToNewYork(nowUtc).Date;

            // Check yesterday/today/tomorrow (New York dates) so windows near midnight are handled.
            for (int d = -1; d <= 1; d++)
            {
                DateTime day = nyToday.AddDays(d);
                if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;

                foreach (var t in _dailyNewsTimes)
                {
                    DateTime eventUtc = NewYorkToUtc(day + t);
                    if (nowUtc >= eventUtc.AddMinutes(-MinutesBeforeNews) && nowUtc <= eventUtc.AddMinutes(MinutesAfterNews))
                    {
                        ev = new NewsEvent
                        {
                            TimeUtc = eventUtc,
                            Title = "Daily US data window (" + t.ToString(@"hh\:mm") + " New York)",
                            Currency = "USD",
                            Impact = "Daily"
                        };
                        return true;
                    }
                }
            }
            return false;
        }

        // US daylight saving: from the 2nd Sunday of March 02:00 to the 1st Sunday of November 02:00 (local).
        private static DateTime NthSunday(int year, int month, int n)
        {
            var first = new DateTime(year, month, 1);
            int offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + 7 * (n - 1));
        }

        private static bool IsNewYorkDst(DateTime nyLocal)
        {
            DateTime start = NthSunday(nyLocal.Year, 3, 2).AddHours(2);
            DateTime end = NthSunday(nyLocal.Year, 11, 1).AddHours(2);
            return nyLocal >= start && nyLocal < end;
        }

        private static DateTime NewYorkToUtc(DateTime nyLocal) =>
            nyLocal.AddHours(IsNewYorkDst(nyLocal) ? 4 : 5);

        private static DateTime UtcToNewYork(DateTime utc)
        {
            int year = utc.Year;
            DateTime dstStartUtc = NthSunday(year, 3, 2).AddHours(2 + 5);   // 02:00 EST
            DateTime dstEndUtc = NthSunday(year, 11, 1).AddHours(2 + 4);    // 02:00 EDT
            bool dst = utc >= dstStartUtc && utc < dstEndUtc;
            return utc.AddHours(dst ? -4 : -5);
        }

        private bool IsNewsBlackout(out NewsEvent ev)
        {
            ev = null;
            var now = Server.TimeInUtc;

            if (_dailyWindowActive && IsInDailyNewsWindow(now, out ev))
                return true;

            if (!_newsActive) return false;
            foreach (var e in _newsEvents)
            {
                if (now >= e.TimeUtc.AddMinutes(-MinutesBeforeNews) && now <= e.TimeUtc.AddMinutes(MinutesAfterNews))
                {
                    ev = e;
                    return true;
                }
            }
            return false;
        }

        private void UpdateNewsState()
        {
            bool blackout = IsNewsBlackout(out var ev);

            if (blackout && !_inNewsBlackout)
            {
                _inNewsBlackout = true;
                Print("NEWS WINDOW START: '{0}' ({1}, {2}) at {3:HH:mm} UTC - no new orders until {4:HH:mm} UTC.",
                    ev.Title, ev.Currency, ev.Impact, ev.TimeUtc, ev.TimeUtc.AddMinutes(MinutesAfterNews));

                // Pending orders are exactly what a news spike fills at a bad price.
                foreach (var f in new[] { ActiveBullFib, ActiveBearFib })
                {
                    if (f != null && f.Order != null)
                    {
                        CancelFibOrder(f, "news window");
                        f.TradeAttempted = false;          // allow it to be placed again after the news
                    }
                }
                foreach (var o in PendingOrders.Where(o => o.Label == TradeLabel && o.SymbolName == SymbolName).ToList())
                    CancelPendingOrder(o);

                if (CloseTradesBeforeNews)
                {
                    foreach (var p in Positions.Where(IsOwnPosition).ToList())
                    {
                        Print("Closing position {0} before news.", p.Id);
                        var r = ClosePosition(p);
                        if (!r.IsSuccessful) Print("Close before news FAILED: {0}", r.Error);
                    }
                }
            }
            else if (!blackout && _inNewsBlackout)
            {
                _inNewsBlackout = false;
                Print("NEWS WINDOW END - trading resumed.");

                // Re-place the order of a setup that is still valid (not touched, not invalidated).
                if (Trend == StructureTrend.Bullish) TryPlaceOrder(ActiveBullFib);
                if (Trend == StructureTrend.Bearish) TryPlaceOrder(ActiveBearFib);
            }
        }

        // =====================================================================
        //  MONTHLY PROFIT TARGET / MONTHLY TRADING LOCK
        //  Uses realized BALANCE (never equity) and server/backtest time (Server.Time).
        // =====================================================================
        private bool _monthlyInitialized;
        private int _monthYear;
        private int _monthNumber;
        private double MonthlyStartingBalance;
        private double MonthlyProfitTarget;
        private double MonthlyTargetBalance;
        private bool MonthlyTargetReached;

        private string MonthKey => string.Format("{0:0000}-{1:00}", _monthYear, _monthNumber);

        private string MonthlyStorageKey =>
            Regex.Replace("MSB_Monthly_" + Account.Number + "_" + SymbolName + "_" + TradeLabel, "[^A-Za-z0-9_]", "_");

        private bool UseMonthlyStorage => RunningMode == RunningMode.RealTime;

        // Every new trade goes through TryPlaceOrder, which calls this first.
        private bool CanOpenNewTrade()
        {
            CheckMonthlyTarget();

            if (MonthlyTargetReached)
            {
                Print("TRADE BLOCKED");
                Print("Reason: Monthly {0}% profit target already reached ({1}).",
                    MonthlyProfitTargetPercent.ToString("0.##", CultureInfo.InvariantCulture), MonthKey);
                Print("Next trading period: Next calendar month.");
                return false;
            }

            return true;
        }

        private void InitMonthlyTarget()
        {
            DateTime now = Server.Time;
            _monthYear = now.Year;
            _monthNumber = now.Month;
            _monthlyInitialized = true;

            // 1) Restore saved state for this same month (live restarts).
            if (UseMonthlyStorage && TryLoadMonthlyState())
            {
                MonthlyProfitTarget = MonthlyStartingBalance * MonthlyProfitTargetPercent / 100.0;
                MonthlyTargetBalance = MonthlyStartingBalance * (1 + MonthlyProfitTargetPercent / 100.0);
                Print("Monthly target: restored saved state for {0} - start {1}, target {2}, {3}.",
                    MonthKey, Money(MonthlyStartingBalance), Money(MonthlyTargetBalance),
                    MonthlyTargetReached ? "TRADING LOCKED" : "trading enabled");
                CheckMonthlyTarget();
                return;
            }

            // 2) Otherwise rebuild this month's starting balance from closed trades of this month.
            DateTime monthStart = new DateTime(_monthYear, _monthNumber, 1);
            double realizedThisMonth = 0;
            int dealsThisMonth = 0;
            try
            {
                foreach (var h in History)
                {
                    if (h.ClosingTime >= monthStart)
                    {
                        realizedThisMonth += h.NetProfit;
                        dealsThisMonth++;
                    }
                }
            }
            catch (Exception ex)
            {
                Print("Monthly target: could not read trade history ({0}).", ex.Message);
                realizedThisMonth = 0;
                dealsThisMonth = 0;
            }

            SetMonth(Account.Balance - realizedThisMonth);
            if (dealsThisMonth > 0)
                Print("Monthly target: starting balance for {0} rebuilt from {1} closed deal(s) this month.",
                    MonthKey, dealsThisMonth);

            PrintNewMonthBlock();
            SaveMonthlyState();
            CheckMonthlyTarget();
        }

        // Called on every tick/bar, after closes and before every new trade.
        private void CheckMonthlyTarget()
        {
            if (!_monthlyInitialized) return;

            DateTime now = Server.Time;
            if (now.Year != _monthYear || now.Month != _monthNumber)
                StartNewMonth(now);

            if (!MonthlyTargetReached && Account.Balance >= MonthlyTargetBalance)
            {
                MonthlyTargetReached = true;
                double profit = Account.Balance - MonthlyStartingBalance;

                Print("=============================================");
                Print("MONTHLY TARGET REACHED");
                Print("Month: {0}", MonthKey);
                Print("Starting Balance: {0}", Money(MonthlyStartingBalance));
                Print("Current Balance: {0}", Money(Account.Balance));
                Print("Profit: {0}", Money(profit));
                Print("Return: {0}%", (profit / MonthlyStartingBalance * 100.0).ToString("0.00", CultureInfo.InvariantCulture));
                Print("MONTHLY TRADING LOCKED");
                Print("NO NEW TRADES UNTIL NEXT MONTH");
                Print("=============================================");

                // A pending order could still be filled later and create a new position - remove them.
                CancelPendingOrdersForMonthlyLock();
                SaveMonthlyState();
            }
        }

        private void StartNewMonth(DateTime now)
        {
            string previousKey = MonthKey;
            _monthYear = now.Year;
            _monthNumber = now.Month;

            SetMonth(Account.Balance);

            Print("=============================================");
            Print("MONTHLY LOCK RESET");
            Print("Previous Month: {0}", previousKey);
            Print("New Month: {0}", MonthKey);
            Print("New Starting Balance: {0}", Money(MonthlyStartingBalance));
            Print("New {0}% Target Balance: {1}",
                MonthlyProfitTargetPercent.ToString("0.##", CultureInfo.InvariantCulture), Money(MonthlyTargetBalance));
            Print("TRADING ENABLED");
            Print("=============================================");
            PrintNewMonthBlock();
            SaveMonthlyState();

            // A setup that is still valid (not touched / not invalidated) may be traded again.
            if (!_loadingHistory && !_inNewsBlackout)
            {
                if (Trend == StructureTrend.Bullish) TryPlaceOrder(ActiveBullFib);
                if (Trend == StructureTrend.Bearish) TryPlaceOrder(ActiveBearFib);
            }
        }

        private void SetMonth(double startingBalance)
        {
            MonthlyStartingBalance = startingBalance;
            MonthlyProfitTarget = MonthlyStartingBalance * MonthlyProfitTargetPercent / 100.0;
            MonthlyTargetBalance = MonthlyStartingBalance * (1 + MonthlyProfitTargetPercent / 100.0);
            MonthlyTargetReached = false;
        }

        private void PrintNewMonthBlock()
        {
            Print("=============================================");
            Print("NEW MONTH STARTED");
            Print("Month: {0}", MonthKey);
            Print("Starting Balance: {0}", Money(MonthlyStartingBalance));
            Print("Monthly Target: {0}%", MonthlyProfitTargetPercent.ToString("0.##", CultureInfo.InvariantCulture));
            Print("Profit Required: {0}", Money(MonthlyProfitTarget));
            Print("Target Balance: {0}", Money(MonthlyTargetBalance));
            Print("TRADING ENABLED");
            Print("=============================================");
        }

        private void CancelPendingOrdersForMonthlyLock()
        {
            foreach (var f in new[] { ActiveBullFib, ActiveBearFib })
            {
                if (f != null && f.Order != null)
                {
                    CancelFibOrder(f, "monthly profit target reached");
                    f.TradeAttempted = false;   // may be placed again next month if still valid
                }
            }

            foreach (var o in PendingOrders.Where(o => o.Label == TradeLabel && o.SymbolName == SymbolName).ToList())
            {
                CancelPendingOrder(o);
                Print("Cancelled pending {0} order: monthly profit target reached", o.TradeType);
            }
        }

        // ---- persistence (live only; backtests always start clean) ----
        private void SaveMonthlyState()
        {
            if (!UseMonthlyStorage) return;
            try
            {
                string value = string.Join("|",
                    MonthKey,
                    MonthlyStartingBalance.ToString("R", CultureInfo.InvariantCulture),
                    MonthlyTargetReached ? "1" : "0");
                LocalStorage.SetString(MonthlyStorageKey, value, LocalStorageScope.Device);
                LocalStorage.Flush(LocalStorageScope.Device);
            }
            catch (Exception ex)
            {
                Print("Monthly target: could not save state ({0}).", ex.Message);
            }
        }

        private bool TryLoadMonthlyState()
        {
            try
            {
                string value = LocalStorage.GetString(MonthlyStorageKey, LocalStorageScope.Device);
                if (string.IsNullOrEmpty(value)) return false;

                var parts = value.Split('|');
                if (parts.Length != 3 || parts[0] != MonthKey) return false;    // saved state is from another month

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) || start <= 0)
                    return false;

                MonthlyStartingBalance = start;
                MonthlyTargetReached = parts[2] == "1";
                return true;
            }
            catch (Exception ex)
            {
                Print("Monthly target: could not load saved state ({0}).", ex.Message);
                return false;
            }
        }

        private static string Money(double value) =>
            "$" + value.ToString("N2", CultureInfo.InvariantCulture);

        // =====================================================================
        //  Drawing - Fibonacci
        // =====================================================================
        private string FibId(FibSetup f) =>
            (f.IsBullish ? "FIB_BULL" : "FIB_BEAR") + (ShowOnlyLatestFib ? "" : "_" + f.Zero.Index);

        private void DrawFib(FibSetup f)
        {
            if (!CanDraw) return;

            string id = FibId(f);
            if (ShowOnlyLatestFib)
            {
                Chart.RemoveObject(id + "_TOUCH");
                Chart.RemoveObject(id + "_TRADE_SL");
                Chart.RemoveObject(id + "_TRADE_TP");
            }

            int start = f.One.Index;
            int end = f.Zero.Index + FibExtendBars;

            for (int n = 0; n < _fibLevels.Count; n++)
            {
                double lvl = _fibLevels[n];
                double price = f.PriceAt(lvl);
                bool golden = lvl >= Math.Min(GoldenStart, GoldenEnd) && lvl <= Math.Max(GoldenStart, GoldenEnd);

                Chart.DrawTrendLine(id + "_L" + n, start, price, end, price,
                    golden ? Color.Gold : Color.Silver, golden ? 2 : 1, LineStyle.Solid);

                var t = Chart.DrawText(id + "_T" + n,
                    lvl.ToString(CultureInfo.InvariantCulture) + " (" + Fmt(price) + ")",
                    start, price, golden ? Color.Gold : Color.Silver);
                t.HorizontalAlignment = HorizontalAlignment.Left;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.FontSize = 9;
            }

            Chart.DrawTrendLine(id + "_DIAG", f.One.Index, f.One.Price, f.Zero.Index, f.Zero.Price,
                Color.Gray, 1, LineStyle.Lines);

            var boxColor = f.IsBullish ? Color.FromArgb(70, 0, 170, 90) : Color.FromArgb(70, 210, 60, 60);
            var box = Chart.DrawRectangle(id + "_ZONE", f.Zero.Index, f.ZoneHigh, end, f.ZoneLow, boxColor);
            box.IsFilled = true;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================
        private string Fmt(double price) => price.ToString("N" + Symbol.Digits, CultureInfo.InvariantCulture);

        private int HighestIndex(int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Max(from, to);
            int best = from;
            for (int k = from + 1; k <= to; k++)
                if (Bars.HighPrices[k] > Bars.HighPrices[best]) best = k;
            return best;
        }

        private int LowestIndex(int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Max(from, to);
            int best = from;
            for (int k = from + 1; k <= to; k++)
                if (Bars.LowPrices[k] < Bars.LowPrices[best]) best = k;
            return best;
        }

        private void Mark(SwingPoint sp, string label, bool isHigh)
        {
            sp.Label = label;
            string key = (isHigh ? "H" : "L") + sp.Index;

            string text = label;
            if (_labels.TryGetValue(key, out var existing) && !existing.EndsWith(label))
                text = existing + "→" + label;
            _labels[key] = text;

            if (PrintToLog && !_loadingHistory)
                Print("{0} at {1}  ({2:yyyy-MM-dd HH:mm})  Trend: {3}", text, Fmt(sp.Price), sp.Time, Trend);

            if (!CanDraw) return;

            var color = label.StartsWith("H") ? Color.LimeGreen : Color.Red;
            var t = Chart.DrawText("MS_" + key, text, sp.Index, sp.Price, color);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = isHigh ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            t.FontSize = 11;
        }

        private void DrawBreak(SwingPoint broken, int breakIndex, string text, bool bullish)
        {
            if (PrintToLog && !_loadingHistory)
                Print("{0} {1} of {2}", bullish ? "Bullish" : "Bearish", text, Fmt(broken.Price));

            if (!CanDraw) return;

            string name = "MS_BRK_" + (bullish ? "U" : "D") + broken.Index;
            Chart.DrawTrendLine(name, broken.Index, broken.Price, breakIndex, broken.Price,
                Color.Teal, 1, text == "CHoCH" ? LineStyle.Dots : LineStyle.Solid);

            var t = Chart.DrawText(name + "_T", text, (broken.Index + breakIndex) / 2, broken.Price, Color.Teal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = bullish ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            t.FontSize = 9;
        }
    }
}
