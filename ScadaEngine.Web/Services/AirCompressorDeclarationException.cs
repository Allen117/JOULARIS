namespace ScadaEngine.Web.Services;

/// <summary>
/// 空壓機能源申報的可預期錯誤 — 帶錯誤代碼，Controller 轉 400 + { code }，
/// 前端以 <c>airdeclaration.err.{code}</c> 依語系翻譯（本頁從一開始就走 i18n，不回傳寫死的中文訊息）。
/// Message 仍保留中文說明供 log 使用。
/// </summary>
public class AirCompressorDeclarationException : InvalidOperationException
{
    public string Code { get; }

    public AirCompressorDeclarationException(string szCode, string szMessage) : base(szMessage)
    {
        Code = szCode;
    }
}
