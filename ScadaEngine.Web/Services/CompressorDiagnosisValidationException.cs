namespace ScadaEngine.Web.Services;

/// <summary>空壓系統診斷設定驗證失敗（Controller 轉 400，前端依 szCode 翻譯 eq.diag.err.*）</summary>
public class CompressorDiagnosisValidationException : Exception
{
    public string szCode { get; }
    public string? szDetail { get; }

    public CompressorDiagnosisValidationException(string szCode, string? szDetail = null) : base(szCode)
    {
        this.szCode = szCode;
        this.szDetail = szDetail;
    }
}
