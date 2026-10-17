using ScadaEngine.Common.Data.Models;
using ScadaEngine.Common.Equipment;
using ScadaEngine.Web.Features.Ems.Models;
using Calc = ScadaEngine.Common.Equipment.EquipmentMetricCalculator;

namespace ScadaEngine.Web.Services;

/// <summary>
/// 設備效率診斷 — 即時指標：從 MQTT 即時快取取各角色現值（× Scale 換算到基準單位）
/// → Common <see cref="EquipmentMetricCalculator"/>（與 Engine 聚合同一組公式）→ 即時 DTO（含評級）。
/// 冰機冷量：FLOW + CHWS + CHWR 三者皆有效 → 流量 + ΔT 計算；否則 RT 點；兩者皆綁時 RT 點只列為對照。
/// 即時快取尚未收到 MQTT（hasData=false）、品質非 Good（含 STALE）→ 視為無值 → 評級 NoData，不顯示 0。
/// </summary>
public class EquipmentRealtimeService
{
    private readonly EquipmentProfileService _profileService;
    private readonly MqttRealtimeSubscriberService _realtime;

    public EquipmentRealtimeService(EquipmentProfileService profileService, MqttRealtimeSubscriberService realtime)
    {
        _profileService = profileService;
        _realtime = realtime;
    }

    public async Task<EquipmentRealtimeDto> GetRealtimeAsync(string szKind)
    {
        var profiles = await _profileService.GetAllAsync(szKind, isEnabledOnly: true);
        var aSids = profiles.SelectMany(p => p.aBindings).Select(b => b.szSID).Distinct().ToList();
        var cache = _realtime.GetRealtimeDataBySids(aSids)
            .Where(x => x.hasData && x.isQualityGood && !double.IsNaN(x.dValue))
            .GroupBy(x => x.szSID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (dValue: g.First().dValue, dtTs: g.First().dtTimestamp),
                StringComparer.OrdinalIgnoreCase);

        return new EquipmentRealtimeDto
        {
            kind = szKind,
            serverTime = DateTime.Now,
            items = profiles.Select(p => BuildItem(p, cache)).ToList(),
        };
    }

    /// <summary>純組裝：設備 + 即時值字典（SID → 原始值）→ DTO（供單元測試）</summary>
    public static EquipmentRealtimeItemDto BuildItem(
        EquipmentProfileModel p, IReadOnlyDictionary<string, (double dValue, DateTime dtTs)> cache)
    {
        DateTime? dtLast = null;
        double? V(string szRole)
        {
            var b = p.GetBinding(szRole);
            if (b == null || !cache.TryGetValue(b.szSID, out var v)) return null;
            if (dtLast == null || v.dtTs > dtLast) dtLast = v.dtTs;
            return v.dValue * (b.dScale == 0 ? 1 : b.dScale);
        }

        var th = ChillerEfficiencyStandard.Resolve(p);
        var dGood = th.dGood;
        var dFair = th.dFair;
        var dKw = V(EquipmentRoles.KW);
        var dRun = V(EquipmentRoles.RUN);
        var dLoad = V(EquipmentRoles.LOAD);
        var item = new EquipmentRealtimeItemDto
        {
            id = p.nId,
            name = p.szName,
            kw = dKw,
            good = dGood,
            fair = dFair,
            thresholdBasis = EquipmentThresholdBasisDto.From(th),
        };

        if (p.szKind == EquipmentKind.Compressor)
        {
            var dFlow = V(EquipmentRoles.AIRFLOW);
            var r = Calc.ComputeCompressorInstant(new CompressorInstantInput
            {
                dKw = dKw,
                dFlow = dFlow,
                dPressBar = V(EquipmentRoles.DISCHPRESS),
                dTempC = V(EquipmentRoles.DISCHTEMP),
                dLoaded = V(EquipmentRoles.LOADED),
                dRun = dRun,
                dLoadPct = dLoad,
                dRatedKw = p.dRatedPower,
                dRatedFlow = p.dRatedOutput,
                dGood = dGood,
                dFair = dFair,
            });
            item.isRunning = IsRunningOrUnknown(dRun, dKw, p.dRatedPower, r.isRunning);
            item.grade = r.grade.ToString();
            item.primary = r.dSpecificPower;
            item.secondary = r.dKwPer100Cfm;
            item.output = dFlow;
            item.loadPct = r.dLoadPct;
            item.pressBar = r.dPressBar;
            item.tempC = r.dTempC;
            item.isLoaded = r.isLoaded;
        }
        else
        {
            var dFlow = V(EquipmentRoles.FLOW);
            var dChws = V(EquipmentRoles.CHWS);
            var dChwr = V(EquipmentRoles.CHWR);
            var dRtPoint = Calc.ClampRtPoint(V(EquipmentRoles.RT));   // 負值當 0（與流量路徑對稱）
            var hasFlowPath = p.GetBinding(EquipmentRoles.FLOW) != null
                && p.GetBinding(EquipmentRoles.CHWS) != null
                && p.GetBinding(EquipmentRoles.CHWR) != null;
            var dRtCalc = hasFlowPath
                ? Calc.CoolingRtFromFlow(dFlow, dChws, dChwr, p.dSpecificHeat ?? Calc.DefaultSpecificHeat)
                : null;
            var dRt = dRtCalc ?? dRtPoint;
            item.outputSource = dRtCalc != null ? "flow" : dRtPoint != null ? "rt" : null;

            var r = Calc.ComputeChillerInstant(new ChillerInstantInput
            {
                dKw = dKw,
                dRt = dRt,
                dChws = dChws,
                dChwr = dChwr,
                dCdws = V(EquipmentRoles.CDWS),
                dCdwr = V(EquipmentRoles.CDWR),
                dEvapT = V(EquipmentRoles.EVAPT),
                dCondT = V(EquipmentRoles.CONDT),
                dRun = dRun,
                dLoadPct = dLoad,
                dRatedRt = p.dRatedOutput,
                dRatedKw = p.dRatedPower,
                dGood = dGood,
                dFair = dFair,
            });
            item.isRunning = IsRunningOrUnknown(dRun, dKw, p.dRatedPower, r.isRunning);
            item.grade = r.grade.ToString();
            item.primary = r.dKwPerRt;
            item.secondary = r.dCop;
            item.tertiary = r.dEer;
            item.output = dRt;
            item.rtPoint = hasFlowPath ? dRtPoint : null;
            item.loadPct = r.dLoadPct;
            item.chws = dChws;
            item.chwr = dChwr;
            item.chwDeltaT = r.dChwDeltaT;
            item.cdwDeltaT = r.dCdwDeltaT;
            item.evapApproach = r.dEvapApproach;
            item.condApproach = r.dCondApproach;
        }

        item.hasData = dtLast != null;
        item.lastUpdate = dtLast;
        if (!item.hasData) item.grade = EquipmentGrade.NoData.ToString();
        return item;
    }

    /// <summary>計算器把「未知」當運轉處理（算得出就算）；DTO 對外區分 true / false / 未知</summary>
    private static bool? IsRunningOrUnknown(double? dRun, double? dKw, double? dRatedKw, bool isRunningFromCalc) =>
        Calc.IsRunning(dRun, dKw, dRatedKw) == null ? null : isRunningFromCalc;
}
