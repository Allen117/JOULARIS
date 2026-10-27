# ScadaEngine.ModbusServer 規則

對外 Modbus TCP Gateway：FC4 float32 供第三方讀即時值 + 對照表網頁。只引用 Common。完整說明 → docs/功能說明書_ModbusServerGateway.md

- **`AddressMap.json` 是 append-only 位址對照**（執行期自動產生、持久化）：既有點位位址只增不改，第三方 SCADA 依它讀值，**升級必保留、勿刪**
- `Setting/ModbusServerSetting.json`：監聽埠(502) / UnitId / WordOrder / NaN 或 HoldLast / 網頁埠 / 位址段界 — **僅啟動載入**，改了要重啟
- 網頁埠 **5041** 是刻意的：5040 被 Windows CDPSvc 佔用
- **獨立安裝**：`QuickDeployAll` / `Install.bat` 不碰它，要裝/更新另跑 Release 包內 `InstallModbusServer.bat`
