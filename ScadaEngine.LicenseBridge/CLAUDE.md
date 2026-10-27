# ScadaEngine.LicenseBridge 規則

HASP USB 加密狗授權驗證的 bridge 服務。完整說明 → docs/架構.md §HASP 授權守衛

- **net48 + `PlatformTarget=x86`（32-bit）**：`hasp_net_windows.dll`（放在 `ScadaEngine.Engine/Lib/`）要在 32-bit .NET Framework 進程載入，這就是本服務獨立於 .NET 8 x64 Engine 存在的原因；目標框架與平台維持原樣
- Engine（`Services/LicenseService.cs`）透過 **Named Pipe** 呼叫本服務（`Services/HaspVerificationService.cs`），不走 HTTP / MQTT
- Engine 端每 **30 分鐘**驗一次，失敗即暫停 Modbus 採集並發 MQTT `SCADA/Sys/License/Status`；改 pipe 協定時兩端要一起改
