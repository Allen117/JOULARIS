namespace ScadaEngine.Web.Services;

/// <summary>驗證失敗（Controller 轉 400，前端依 szCode 翻譯）</summary>
public class EquipmentProfileValidationException : Exception
{
    public string szCode { get; }
    public string? szDetail { get; }

    public EquipmentProfileValidationException(string szCode, string? szDetail = null) : base(szCode)
    {
        this.szCode = szCode;
        this.szDetail = szDetail;
    }
}
