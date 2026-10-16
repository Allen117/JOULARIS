using ScadaEngine.Common.Data.Models;

namespace ScadaEngine.Common.Equipment;

/// <summary>冰機冷卻方式（EquipmentProfile.CoolingType）</summary>
public static class ChillerCoolingType
{
    public const string Water = "water";
    public const string Air = "air";

    public static readonly string[] All = [Water, Air];

    public static bool IsValid(string? sz) => sz == Water || sz == Air;
}

/// <summary>
/// 冰機壓縮機形式（EquipmentProfile.ChillerType）。
/// 90.1 只分離心 / 容積式（positive displacement）— 螺旋 / 渦卷 / 往復三者同表，UI 仍讓使用者依現場認知選。
/// </summary>
public static class ChillerCompressorType
{
    public const string Centrifugal = "centrifugal";
    public const string Screw = "screw";
    public const string Scroll = "scroll";
    public const string Reciprocating = "reciprocating";

    public static readonly string[] All = [Centrifugal, Screw, Scroll, Reciprocating];

    public static bool IsValid(string? sz) => sz is Centrifugal or Screw or Scroll or Reciprocating;
}

/// <summary>
/// 評級門檻模式（EquipmentProfile.ThresholdMode，僅冰機）：standard = 依 ASHRAE 90.1 表自動帶出；manual = 用 Good/Fair 欄位。
/// NULL 視為 standard（冰機預設依標準）。空壓機一律手動門檻，本欄不使用。
/// </summary>
public static class EquipmentThresholdMode
{
    public const string Standard = "standard";
    public const string Manual = "manual";

    public static bool IsValid(string? sz) => sz == Standard || sz == Manual;
}

/// <summary>90.1 表的一列：冷卻 × 系列（centrifugal / pd / air）× 噸數級距 [dMinRt, dMaxRt)</summary>
/// <param name="dPathAFl">Path A（定頻）滿載 kW/RT</param>
/// <param name="dPathBFl">Path B（變頻）滿載 kW/RT</param>
/// <param name="dPathAIplv">Path A IPLV kW/RT（僅供文件 / 對照，不參與評級）</param>
/// <param name="dPathBIplv">Path B IPLV kW/RT（同上）</param>
public sealed record ChillerStandardBand(
    string szFamily, double dMinRt, double? dMaxRt,
    double dPathAFl, double dPathBFl, double dPathAIplv, double dPathBIplv);

/// <summary>
/// 門檻解析結果 — 評級用 Good / Fair + 依據（給前端組依據字串，例「90.1 水冷離心 400–600RT 定頻(Path A)」）。
/// </summary>
public sealed class EquipmentThresholdResult
{
    /// <summary>主指標 ≤ Good → 優</summary>
    public double dGood { get; init; }
    /// <summary>主指標 ≤ Fair → 可，&gt; Fair → 差</summary>
    public double dFair { get; init; }
    /// <summary>standard / manual</summary>
    public string szMode { get; init; } = EquipmentThresholdMode.Manual;

    // ── 以下僅 standard 有值 ──
    /// <summary>90.1 標準滿載值 kW/RT（= Good）</summary>
    public double? dStandardFl { get; init; }
    /// <summary>water / air（機型未設定時為推定的 water）</summary>
    public string? szCoolingType { get; init; }
    /// <summary>centrifugal / screw / scroll / reciprocating（氣冷可為 null）</summary>
    public string? szChillerType { get; init; }
    /// <summary>90.1 系列：centrifugal / pd / air</summary>
    public string? szFamily { get; init; }
    /// <summary>A（定頻）/ B（變頻）</summary>
    public string? szPath { get; init; }
    public double? dBandMinRt { get; init; }
    /// <summary>null = 無上限（≥ dBandMinRt）</summary>
    public double? dBandMaxRt { get; init; }
    /// <summary>冷卻方式 / 形式 / 變頻任一未設定 → 以水冷離心定頻補位</summary>
    public bool isTypeUnset { get; init; }
    /// <summary>未填額定 RT → 取該系列最寬鬆（最小噸數）級距</summary>
    public bool isRatedMissing { get; init; }
}

/// <summary>
/// 冰機評級門檻 — ASHRAE 90.1-2022 Table 6.8.1-3（與 2019 版數值相同）最低效率要求；**門檻解析唯一真相**。
/// 評級：優 ≤ FL（標準滿載值）、可 ≤ FL × <see cref="FairFactor"/>、差 &gt; 其上。
/// FL 依 冷卻方式 × 形式（離心 / 容積式；氣冷不分）× 額定 RT 級距 × 路徑（定頻 → Path A、變頻 → Path B，業界對應慣例）。
/// 數值為 AHRI 550/590 標準工況（冷卻水 29.4°C 進水）新機最低要求；不做 Kadj 非標準工況修正。
/// 氣冷原表為 EER，換算 kW/RT = 12 ÷ EER 後取三位小數。
/// 即時、期間排名、EMS 首頁卡皆經 <see cref="Resolve"/>，門檻不寫回 DB（換新版標準只改本檔常數表）。
/// </summary>
public static class ChillerEfficiencyStandard
{
    /// <summary>依據字串用的標準名稱</summary>
    public const string StandardName = "ASHRAE 90.1-2022 Table 6.8.1-3";

    /// <summary>可 / 差分界 = FL × 此係數（容許 15% 現場劣化帶；日後要調只改此處）</summary>
    public const double FairFactor = 1.15;

    public const string FamilyCentrifugal = "centrifugal";
    public const string FamilyPositiveDisplacement = "pd";
    public const string FamilyAir = "air";

    public const string PathA = "A";
    public const string PathB = "B";

    private static double FromEer(double dEer) => Math.Round(12.0 / dEer, 3);

    /// <summary>90.1-2022 Table 6.8.1-3（I-P 版）；同系列依 dMinRt 遞增排列，第一列 = 最寬鬆級距</summary>
    public static readonly IReadOnlyList<ChillerStandardBand> Bands =
    [
        // 水冷離心
        new(FamilyCentrifugal, 0,   150,  0.610, 0.695, 0.550, 0.440),
        new(FamilyCentrifugal, 150, 300,  0.610, 0.635, 0.550, 0.400),
        new(FamilyCentrifugal, 300, 400,  0.560, 0.595, 0.520, 0.390),
        new(FamilyCentrifugal, 400, 600,  0.560, 0.585, 0.500, 0.380),
        new(FamilyCentrifugal, 600, null, 0.560, 0.585, 0.500, 0.380),
        // 水冷容積式（螺旋 / 渦卷 / 往復）
        new(FamilyPositiveDisplacement, 0,   75,   0.750, 0.780, 0.600, 0.500),
        new(FamilyPositiveDisplacement, 75,  150,  0.720, 0.750, 0.560, 0.490),
        new(FamilyPositiveDisplacement, 150, 300,  0.660, 0.680, 0.540, 0.440),
        new(FamilyPositiveDisplacement, 300, 600,  0.610, 0.625, 0.520, 0.410),
        new(FamilyPositiveDisplacement, 600, null, 0.560, 0.585, 0.500, 0.380),
        // 氣冷（全形式；原表 EER：FL A 10.1 / B 9.7、IPLV <150 A 13.7 / B 15.8、≥150 A 14.0 / B 16.1）
        new(FamilyAir, 0,   150,  FromEer(10.1), FromEer(9.7), FromEer(13.7), FromEer(15.8)),
        new(FamilyAir, 150, null, FromEer(10.1), FromEer(9.7), FromEer(14.0), FromEer(16.1)),
    ];

    /// <summary>冷卻方式 + 形式 → 90.1 系列</summary>
    public static string FamilyOf(string szCoolingType, string? szChillerType) =>
        szCoolingType == ChillerCoolingType.Air ? FamilyAir
        : szChillerType is null or ChillerCompressorType.Centrifugal ? FamilyCentrifugal
        : FamilyPositiveDisplacement;

    /// <summary>系列 + 額定 RT → 級距；額定 RT 無效（null / ≤0）→ 該系列最寬鬆級距</summary>
    public static ChillerStandardBand FindBand(string szFamily, double? dRatedRt)
    {
        var list = Bands.Where(b => b.szFamily == szFamily).ToList();
        if (dRatedRt is not > 0) return list[0];
        return list.LastOrDefault(b => dRatedRt.Value >= b.dMinRt) ?? list[0];
    }

    /// <summary>
    /// 解析一台設備的評級門檻。空壓機 / 冰機手動模式 → 欄位值（缺 → <see cref="EquipmentKind.DefaultThresholds"/>）；
    /// 冰機依標準模式（含 ThresholdMode NULL）→ 查 90.1 表。
    /// </summary>
    public static EquipmentThresholdResult Resolve(EquipmentProfileModel p)
    {
        if (p.szKind != EquipmentKind.Chiller || p.szThresholdMode == EquipmentThresholdMode.Manual)
        {
            var (dDefGood, dDefFair) = EquipmentKind.DefaultThresholds(p.szKind);
            return new EquipmentThresholdResult
            {
                dGood = p.dGoodThreshold ?? dDefGood,
                dFair = p.dFairThreshold ?? dDefFair,
                szMode = EquipmentThresholdMode.Manual,
            };
        }

        var isCoolingSet = ChillerCoolingType.IsValid(p.szCoolingType);
        var szCooling = isCoolingSet ? p.szCoolingType! : ChillerCoolingType.Water;
        var isTypeSet = ChillerCompressorType.IsValid(p.szChillerType);
        var szType = isTypeSet ? p.szChillerType : (szCooling == ChillerCoolingType.Air ? null : ChillerCompressorType.Centrifugal);
        var isControlSet = p.szControlType is ChillerControl.Fixed or ChillerControl.Vsd;
        var szPath = p.szControlType == ChillerControl.Vsd ? PathB : PathA;
        // 氣冷 90.1 不分形式 → 形式未選不算缺
        var isTypeUnset = !isCoolingSet || !isControlSet || (szCooling == ChillerCoolingType.Water && !isTypeSet);

        var szFamily = FamilyOf(szCooling, szType);
        var band = FindBand(szFamily, p.dRatedOutput);
        var dFl = szPath == PathB ? band.dPathBFl : band.dPathAFl;
        return new EquipmentThresholdResult
        {
            dGood = dFl,
            dFair = Math.Round(dFl * FairFactor, 3),
            szMode = EquipmentThresholdMode.Standard,
            dStandardFl = dFl,
            szCoolingType = szCooling,
            szChillerType = szType,
            szFamily = szFamily,
            szPath = szPath,
            dBandMinRt = band.dMinRt,
            dBandMaxRt = band.dMaxRt,
            isTypeUnset = isTypeUnset,
            isRatedMissing = p.dRatedOutput is not > 0,
        };
    }

    /// <summary>冰機 ControlType 允許值（沿用 EquipmentProfile.ControlType 欄位；空壓機另有 modulation）</summary>
    public static class ChillerControl
    {
        public const string Fixed = "fixed";
        public const string Vsd = "vsd";

        public static bool IsValid(string? sz) => sz == Fixed || sz == Vsd;
    }
}
