# MQTT 即時資料訂閱

Engine 會把每個點位的即時值發布到 MQTT Broker。外部系統（MES、雲端平台、自建程式）只要訂閱對應主題，就能取得本系統的即時資料。本章提供給整合廠商的對接資訊，以及工程師需要知道的系統主題。

## Broker 連線資訊

| 項目 | 值 |
|------|-----|
| Broker | 預設 `127.0.0.1`，即與 Engine 同機的 broker |
| Port | `1883`（未加密） |
| 認證 | 無帳號密碼 |
| 協定 | MQTT v5 / v3.1.1 皆可 |

Engine 連線的 broker 設定在 `C:\JOULARIS\Engine\App\MqttSetting\MqttSetting.json`（Web、Gateway 各有自己的 `MqttSetting\MqttSetting.json`，三者的連線端 ID 刻意不同，請勿互相複製覆蓋）。

> ⚠️ 注意：broker 預設沒有帳密與加密。若要讓其他電腦訂閱，需在 broker 開放對外連線並以防火牆限制來源 IP，只開放給可信任的整合主機。

## 主題結構

```
SCADA/Realtime/{coordinatorName}/{SID}
```

| 段落 | 說明 |
|------|------|
| `SCADA/Realtime` | 固定前綴 |
| `{coordinatorName}` | 來源名稱：Modbus 為設備名稱（設定檔名）、DB / OPC UA 為來源名稱、計算點位為其群組名稱（未設定為 `Calculated`） |
| `{SID}` | 點位識別碼，例 `196865-S1`、`DB1-S5`、`OPC1-S3`、`CALC-S2` |

訂閱範例：

| 目的 | 主題 |
|------|------|
| 全部點位 | `SCADA/Realtime/+/+` |
| 某台設備的全部點位 | `SCADA/Realtime/CH_Chiller/+` |
| 單一點位 | `SCADA/Realtime/CH_Chiller/196865-S1` |

## Payload 格式

UTF-8 JSON，欄位為小寫開頭：

```json
{
  "sid": "196865-S1",
  "coordinatorName": "CH_Chiller",
  "name": "CH1MOA",
  "value": 6.0,
  "unit": "",
  "quality": "Good",
  "timestamp": 1740571681000,
  "address": 0
}
```

| 欄位 | 型別 | 說明 |
|------|------|------|
| `sid` | string | 點位識別碼 |
| `coordinatorName` | string | 來源名稱 |
| `name` | string | 點位名稱 |
| `value` | number | 工程值（已套用倍率） |
| `unit` | string | 單位，無單位為空字串 |
| `quality` | string | `Good` / `Bad` / `Uncertain` |
| `timestamp` | number | Unix 毫秒時間戳記（UTC） |
| `address` | number | Modbus 暫存器位址（Modbus 點位適用） |

## 發布特性

| 項目 | 值 |
|------|-----|
| QoS | 1（至少一次） |
| Retain | true |
| 發布時機 | 值或品質變化時立即發布；值沒變超過 3 分鐘也會重發一次 |

因為 Retain = true，新訂閱者一連上就會立刻收到每個點位最後一筆值，不必等下一次變化。

> 💡 提示：值沒變化時最長約 3 分鐘才會收到一次，整合端判斷「斷線」的門檻請設得比 3 分鐘長（系統自身用 5 分鐘）。

## 給整合廠商的資料

交付時請提供：

1. Broker IP 與埠
2. 要訂閱的主題（多半是 `SCADA/Realtime/+/+`）
3. 點位清單（SID、名稱、單位）：在 SSMS 查詢（Excel 匯出不含 SID 欄）：

```sql
SELECT SID, Name, Unit FROM ModbusPoints
UNION ALL SELECT SID, Name, Unit FROM DBPoints
UNION ALL SELECT SID, Name, Unit FROM OpcUaPoints
ORDER BY SID;
```

4. 本章的 Payload 格式說明

訂閱程式範例（Python，paho-mqtt）：

```python
import json
import paho.mqtt.client as mqtt

def on_connect(client, userdata, flags, rc, properties=None):
    client.subscribe("SCADA/Realtime/+/+")

def on_message(client, userdata, msg):
    d = json.loads(msg.payload.decode("utf-8"))
    print(d["sid"], d["name"], d["value"], d["unit"], d["quality"])

client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
client.on_connect = on_connect
client.on_message = on_message
client.connect("127.0.0.1", 1883, keepalive=60)
client.loop_forever()
```

> 💡 提示：若第三方只支援 Modbus，不支援 MQTT，改用 [對外 Modbus TCP Gateway](modbus-gateway)。

## 系統內部主題（工程師參考）

以下主題由系統內部使用，整合廠商**不應發布**到這些主題：

| 主題 | 用途 |
|------|------|
| `SCADA/Control/{SID}` | 監控畫面下達的控制指令 |
| `SCADA/Alarm/Active/{SID}/{type}` | 未恢復警報（Retain；恢復時清空） |
| `SCADA/Sys/AlarmRules/Reload` | 警報規則變更後通知 Engine 重載 |
| `SCADA/Sys/DbCoordinator/Reload` | DB 來源變更後通知 Engine 重載 |
| `SCADA/Sys/OpcUaCoordinator/Reload` | OPC UA 來源變更後通知 Engine 重載 |
| `SCADA/Sys/Notify/Reload` / `Status` | Email / 簡訊 / Line 設定熱重載與回報 |
| `SCADA/Sys/Sms/Cmd` / `Status` | 簡訊測試發送、重掃序列埠 |
| `SCADA/Sys/License/Status` / `Verify` | 授權狀態（Retain）與手動重新驗證 |

> ⚠️ 注意：`SCADA/Control/#` 會被 Engine 當成控制指令執行並寫入現場設備。broker 對外開放時，務必確保外部系統無法發布到此主題。

## 常見狀況

| 狀況 | 原因 / 處理 |
|------|------------|
| 訂閱後完全沒收到資料 | 確認 `JoularisEngine` 正在執行，以及 broker 可連線（`telnet <IP> 1883`） |
| `name` 欄位顯示成 SID | broker 上殘留舊版 Engine 的保留訊息；將 Engine 與 broker 一起重啟即可清除 |
| 已刪除的點位仍收到一筆舊值 | broker 保留訊息，同上 |
| `value` 有微小誤差 | 系統內部以 32 位元浮點運算，請在整合端四捨五入到合理小數位 |
