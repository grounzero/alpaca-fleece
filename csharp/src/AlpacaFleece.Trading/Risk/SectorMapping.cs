namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Static symbol-to-sector and symbol-to-asset-class lookup tables.
/// Used by CorrelationService for concentration limit checks.
///
/// Sector names follow GICS (Global Industry Classification Standard).
/// Symbols not in the mapping fall back to UnknownSector / AssetClass.Equity.
/// </summary>
public static class SectorMapping
{
    public const string UnknownSector = "Unknown";
    public const string Technology = "Technology";
    public const string CommunicationServices = "CommunicationServices";
    public const string ConsumerDiscretionary = "ConsumerDiscretionary";
    public const string ConsumerStaples = "ConsumerStaples";
    public const string Healthcare = "Healthcare";
    public const string Financials = "Financials";
    public const string Energy = "Energy";
    public const string Industrials = "Industrials";
    public const string Materials = "Materials";
    public const string Utilities = "Utilities";
    public const string RealEstate = "RealEstate";
    public const string Equities = "Equities";
    public const string Bonds = "Bonds";
    public const string Commodities = "Commodities";
    public const string Crypto = "Crypto";

    /// <summary>Symbol → GICS sector string.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sectors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Technology ────────────────────────────────────────────────────────
            ["AAPL"] = Technology,
            ["MSFT"] = Technology,
            ["NVDA"] = Technology,
            ["AMD"] = Technology,
            ["INTC"] = Technology,
            ["CRM"] = Technology,
            ["ORCL"] = Technology,
            ["ADBE"] = Technology,
            ["NOW"] = Technology,
            ["QCOM"] = Technology,
            ["TXN"] = Technology,
            ["AMAT"] = Technology,
            ["LRCX"] = Technology,
            ["KLAC"] = Technology,
            ["MU"] = Technology,
            ["AVGO"] = Technology,
            ["XLK"] = Technology,   // Technology Select Sector SPDR ETF

            // ── Communication Services ────────────────────────────────────────────
            ["GOOG"] = CommunicationServices,
            ["GOOGL"] = CommunicationServices,
            ["META"] = CommunicationServices,
            ["NFLX"] = CommunicationServices,
            ["DIS"] = CommunicationServices,
            ["T"] = CommunicationServices,
            ["VZ"] = CommunicationServices,
            ["CMCSA"] = CommunicationServices,
            ["XLC"] = CommunicationServices,  // Communication Services Select Sector SPDR ETF

            // ── Consumer Discretionary ────────────────────────────────────────────
            ["AMZN"] = ConsumerDiscretionary,
            ["TSLA"] = ConsumerDiscretionary,
            ["HD"] = ConsumerDiscretionary,
            ["MCD"] = ConsumerDiscretionary,
            ["NKE"] = ConsumerDiscretionary,
            ["SBUX"] = ConsumerDiscretionary,
            ["TGT"] = ConsumerDiscretionary,
            ["BKNG"] = ConsumerDiscretionary,
            ["ABNB"] = ConsumerDiscretionary,
            ["XLY"] = ConsumerDiscretionary,   // Consumer Discretionary Select Sector SPDR ETF

            // ── Consumer Staples ──────────────────────────────────────────────────
            ["WMT"] = ConsumerStaples,
            ["PG"] = ConsumerStaples,
            ["KO"] = ConsumerStaples,
            ["PEP"] = ConsumerStaples,
            ["COST"] = ConsumerStaples,
            ["XLP"] = ConsumerStaples,   // Consumer Staples Select Sector SPDR ETF

            // ── Healthcare ────────────────────────────────────────────────────────
            ["JNJ"] = Healthcare,
            ["PFE"] = Healthcare,
            ["UNH"] = Healthcare,
            ["MRK"] = Healthcare,
            ["ABBV"] = Healthcare,
            ["TMO"] = Healthcare,
            ["DHR"] = Healthcare,
            ["BMY"] = Healthcare,
            ["MRNA"] = Healthcare,
            ["LLY"] = Healthcare,
            ["XLV"] = Healthcare,   // Health Care Select Sector SPDR ETF

            // ── Financials ────────────────────────────────────────────────────────
            ["JPM"] = Financials,
            ["BAC"] = Financials,
            ["WFC"] = Financials,
            ["GS"] = Financials,
            ["MS"] = Financials,
            ["AXP"] = Financials,
            ["V"] = Financials,
            ["MA"] = Financials,
            ["BRK.B"] = Financials,
            ["BRK/B"] = Financials,
            ["C"] = Financials,
            ["XLF"] = Financials,   // Financial Select Sector SPDR ETF

            // ── Energy ────────────────────────────────────────────────────────────
            ["XOM"] = Energy,
            ["CVX"] = Energy,
            ["COP"] = Energy,
            ["SLB"] = Energy,
            ["OXY"] = Energy,
            ["XLE"] = Energy,   // Energy Select Sector SPDR ETF

            // ── Industrials ───────────────────────────────────────────────────────
            ["CAT"] = Industrials,
            ["GE"] = Industrials,
            ["MMM"] = Industrials,
            ["BA"] = Industrials,
            ["HON"] = Industrials,
            ["RTX"] = Industrials,
            ["UNP"] = Industrials,
            ["LMT"] = Industrials,
            ["XLI"] = Industrials,   // Industrial Select Sector SPDR ETF

            // ── Materials ─────────────────────────────────────────────────────────
            ["FCX"] = Materials,
            ["NEM"] = Materials,
            ["XLB"] = Materials,   // Materials Select Sector SPDR ETF

            // ── Utilities ─────────────────────────────────────────────────────────
            ["NEE"] = Utilities,
            ["DUK"] = Utilities,
            ["SO"] = Utilities,
            ["XLU"] = Utilities,   // Utilities Select Sector SPDR ETF

            // ── Real Estate ───────────────────────────────────────────────────────
            ["AMT"] = RealEstate,
            ["PLD"] = RealEstate,
            ["EQIX"] = RealEstate,
            ["SPG"] = RealEstate,
            ["VNQ"] = RealEstate,   // Vanguard Real Estate ETF
            ["IYR"] = RealEstate,   // iShares U.S. Real Estate ETF
            ["SCHH"] = RealEstate,  // Schwab U.S. REIT ETF
            ["XLRE"] = RealEstate,  // Real Estate Select Sector SPDR ETF

            // ── Broad Equity ETFs ─────────────────────────────────────────────────
            ["SPY"] = Equities,   // SPDR S&P 500 ETF
            ["QQQ"] = Equities,   // Invesco Nasdaq-100 ETF
            ["IVV"] = Equities,   // iShares Core S&P 500 ETF
            ["VOO"] = Equities,   // Vanguard S&P 500 ETF
            ["VTI"] = Equities,   // Vanguard Total Stock Market ETF
            ["DIA"] = Equities,   // SPDR Dow Jones Industrial Average ETF
            ["IWM"] = Equities,   // iShares Russell 2000 ETF
            ["RSP"] = Equities,   // Invesco S&P 500 Equal Weight ETF
            ["MDY"] = Equities,   // SPDR S&P MidCap 400 ETF

            // ── Bond ETFs ─────────────────────────────────────────────────────────
            ["TLT"] = Bonds,   // iShares 20+ Year Treasury Bond ETF
            ["IEF"] = Bonds,   // iShares 7-10 Year Treasury Bond ETF
            ["SHY"] = Bonds,   // iShares 1-3 Year Treasury Bond ETF
            ["AGG"] = Bonds,   // iShares Core U.S. Aggregate Bond ETF
            ["BND"] = Bonds,   // Vanguard Total Bond Market ETF
            ["LQD"] = Bonds,   // iShares iBoxx Investment Grade Corporate Bond ETF
            ["HYG"] = Bonds,   // iShares iBoxx High Yield Corporate Bond ETF
            ["JNK"] = Bonds,   // SPDR Bloomberg High Yield Bond ETF
            ["MUB"] = Bonds,   // iShares National Muni Bond ETF
            ["TIP"] = Bonds,   // iShares TIPS Bond ETF
            ["GOVT"] = Bonds,  // iShares U.S. Treasury Bond ETF
            ["VGIT"] = Bonds,  // Vanguard Intermediate-Term Treasury ETF
            ["VGSH"] = Bonds,  // Vanguard Short-Term Treasury ETF
            ["VGLT"] = Bonds,  // Vanguard Long-Term Treasury ETF

            // ── Commodity ETFs ────────────────────────────────────────────────────
            ["GLD"] = Commodities,   // SPDR Gold Shares
            ["IAU"] = Commodities,   // iShares Gold Trust
            ["SLV"] = Commodities,   // iShares Silver Trust
            ["USO"] = Commodities,   // United States Oil Fund
            ["GDX"] = Commodities,   // VanEck Gold Miners ETF
            ["PDBC"] = Commodities,  // Invesco Optimum Yield Diversified Commodity ETF
            ["CPER"] = Commodities,  // United States Copper Index Fund

            // ── Crypto ────────────────────────────────────────────────────────────
            ["BTC/USD"] = Crypto,
            ["ETH/USD"] = Crypto,
            ["LTC/USD"] = Crypto,
            ["BCH/USD"] = Crypto,
            ["DOGE/USD"] = Crypto,
            ["SOL/USD"] = Crypto,
            ["LINK/USD"] = Crypto,
            ["XRP/USD"] = Crypto,
            ["AVAX/USD"] = Crypto,
            ["SHIB/USD"] = Crypto,
        };

    /// <summary>Symbol → broad AssetClass.</summary>
    public static readonly IReadOnlyDictionary<string, AssetClass> AssetClasses =
        new Dictionary<string, AssetClass>(StringComparer.OrdinalIgnoreCase)
        {
            // Technology, Communication Services, Consumer, Healthcare, Financials,
            // Energy, Industrials, Materials, Utilities → Equity
            ["AAPL"] = AssetClass.Equity, ["MSFT"] = AssetClass.Equity,
            ["NVDA"] = AssetClass.Equity, ["AMD"] = AssetClass.Equity,
            ["INTC"] = AssetClass.Equity, ["CRM"] = AssetClass.Equity,
            ["ORCL"] = AssetClass.Equity, ["ADBE"] = AssetClass.Equity,
            ["NOW"] = AssetClass.Equity,  ["QCOM"] = AssetClass.Equity,
            ["TXN"] = AssetClass.Equity,  ["AMAT"] = AssetClass.Equity,
            ["LRCX"] = AssetClass.Equity, ["KLAC"] = AssetClass.Equity,
            ["MU"] = AssetClass.Equity,   ["AVGO"] = AssetClass.Equity,
            ["GOOG"] = AssetClass.Equity, ["GOOGL"] = AssetClass.Equity,
            ["META"] = AssetClass.Equity, ["NFLX"] = AssetClass.Equity,
            ["DIS"] = AssetClass.Equity,  ["T"] = AssetClass.Equity,
            ["VZ"] = AssetClass.Equity,   ["CMCSA"] = AssetClass.Equity,
            ["AMZN"] = AssetClass.Equity, ["TSLA"] = AssetClass.Equity,
            ["HD"] = AssetClass.Equity,   ["MCD"] = AssetClass.Equity,
            ["NKE"] = AssetClass.Equity,  ["SBUX"] = AssetClass.Equity,
            ["TGT"] = AssetClass.Equity,  ["BKNG"] = AssetClass.Equity,
            ["ABNB"] = AssetClass.Equity, ["WMT"] = AssetClass.Equity,
            ["PG"] = AssetClass.Equity,   ["KO"] = AssetClass.Equity,
            ["PEP"] = AssetClass.Equity,  ["COST"] = AssetClass.Equity,
            ["JNJ"] = AssetClass.Equity,  ["PFE"] = AssetClass.Equity,
            ["UNH"] = AssetClass.Equity,  ["MRK"] = AssetClass.Equity,
            ["ABBV"] = AssetClass.Equity, ["TMO"] = AssetClass.Equity,
            ["DHR"] = AssetClass.Equity,  ["BMY"] = AssetClass.Equity,
            ["MRNA"] = AssetClass.Equity, ["LLY"] = AssetClass.Equity,
            ["JPM"] = AssetClass.Equity,  ["BAC"] = AssetClass.Equity,
            ["WFC"] = AssetClass.Equity,  ["GS"] = AssetClass.Equity,
            ["MS"] = AssetClass.Equity,   ["AXP"] = AssetClass.Equity,
            ["V"] = AssetClass.Equity,    ["MA"] = AssetClass.Equity,
            ["BRK.B"] = AssetClass.Equity,["BRK/B"] = AssetClass.Equity,
            ["C"] = AssetClass.Equity,    ["XOM"] = AssetClass.Equity,
            ["CVX"] = AssetClass.Equity,  ["COP"] = AssetClass.Equity,
            ["SLB"] = AssetClass.Equity,  ["OXY"] = AssetClass.Equity,
            ["CAT"] = AssetClass.Equity,  ["GE"] = AssetClass.Equity,
            ["MMM"] = AssetClass.Equity,  ["BA"] = AssetClass.Equity,
            ["HON"] = AssetClass.Equity,  ["RTX"] = AssetClass.Equity,
            ["UNP"] = AssetClass.Equity,  ["LMT"] = AssetClass.Equity,
            ["FCX"] = AssetClass.Equity,  ["NEM"] = AssetClass.Equity,
            ["NEE"] = AssetClass.Equity,  ["DUK"] = AssetClass.Equity,
            ["SO"] = AssetClass.Equity,   ["AMT"] = AssetClass.Equity,
            ["PLD"] = AssetClass.Equity,  ["EQIX"] = AssetClass.Equity,
            ["SPG"] = AssetClass.Equity,

            // Equity ETFs (broad market + sector)
            ["SPY"] = AssetClass.Equity, ["QQQ"] = AssetClass.Equity,
            ["IVV"] = AssetClass.Equity, ["VOO"] = AssetClass.Equity,
            ["VTI"] = AssetClass.Equity, ["DIA"] = AssetClass.Equity,
            ["IWM"] = AssetClass.Equity, ["RSP"] = AssetClass.Equity,
            ["MDY"] = AssetClass.Equity, ["XLK"] = AssetClass.Equity,
            ["XLC"] = AssetClass.Equity, ["XLY"] = AssetClass.Equity,
            ["XLP"] = AssetClass.Equity, ["XLV"] = AssetClass.Equity,
            ["XLF"] = AssetClass.Equity, ["XLE"] = AssetClass.Equity,
            ["XLI"] = AssetClass.Equity, ["XLB"] = AssetClass.Equity,
            ["XLU"] = AssetClass.Equity, ["XLRE"] = AssetClass.Equity,

            // Bond ETFs
            ["TLT"] = AssetClass.Bond, ["IEF"] = AssetClass.Bond,
            ["SHY"] = AssetClass.Bond, ["AGG"] = AssetClass.Bond,
            ["BND"] = AssetClass.Bond, ["LQD"] = AssetClass.Bond,
            ["HYG"] = AssetClass.Bond, ["JNK"] = AssetClass.Bond,
            ["MUB"] = AssetClass.Bond, ["TIP"] = AssetClass.Bond,
            ["GOVT"] = AssetClass.Bond,["VGIT"] = AssetClass.Bond,
            ["VGSH"] = AssetClass.Bond,["VGLT"] = AssetClass.Bond,

            // Real Estate ETFs
            ["VNQ"] = AssetClass.RealEstate, ["IYR"] = AssetClass.RealEstate,
            ["SCHH"] = AssetClass.RealEstate,["XLRE"] = AssetClass.RealEstate,

            // Commodity ETFs
            ["GLD"] = AssetClass.Commodity,  ["IAU"] = AssetClass.Commodity,
            ["SLV"] = AssetClass.Commodity,  ["USO"] = AssetClass.Commodity,
            ["GDX"] = AssetClass.Commodity,  ["PDBC"] = AssetClass.Commodity,
            ["CPER"] = AssetClass.Commodity,

            // Crypto
            ["BTC/USD"] = AssetClass.Crypto, ["ETH/USD"] = AssetClass.Crypto,
            ["LTC/USD"] = AssetClass.Crypto, ["BCH/USD"] = AssetClass.Crypto,
            ["DOGE/USD"] = AssetClass.Crypto,["SOL/USD"] = AssetClass.Crypto,
            ["LINK/USD"] = AssetClass.Crypto,["XRP/USD"] = AssetClass.Crypto,
            ["AVAX/USD"] = AssetClass.Crypto,["SHIB/USD"] = AssetClass.Crypto,
        };

    /// <summary>
    /// Returns the GICS sector for a symbol, or <see cref="UnknownSector"/> if unmapped.
    /// </summary>
    public static string GetSector(string symbol) =>
        Sectors.TryGetValue(symbol, out var sector) ? sector : UnknownSector;

    /// <summary>
    /// Returns the broad asset class for a symbol, or <see cref="AssetClass.Equity"/> if unmapped.
    /// </summary>
    public static AssetClass GetAssetClass(string symbol) =>
        AssetClasses.TryGetValue(symbol, out var cls) ? cls : AssetClass.Equity;
}
