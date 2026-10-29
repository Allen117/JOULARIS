using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ScadaEngine.Engine.Communication.Modbus.Models;
using ScadaEngine.Engine.Communication.Modbus.Services;

namespace ScadaEngine.Tests.Modbus;

/// <summary>
/// Engine 讀 Modbus JSON 的選填欄位：DeviceName（沒有欄位 = null → 不動 DB；空字串 = 明確清空）、
/// RetryCount / NoResponseReconnectRounds（沒寫用預設、超出範圍夾住）。
/// MapJsonToConfigModel 為 private 且不碰 DB，以反射呼叫。
/// </summary>
public class ModbusConfigJsonMappingTests
{
    private static ModbusDeviceConfigModel Map(string szJson)
    {
        var service = new ModbusConfigService(NullLogger<ModbusConfigService>.Instance, null!);
        var config = new ModbusDeviceConfigModel();
        typeof(ModbusConfigService)
            .GetMethod("MapJsonToConfigModel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, new object[] { config, szJson });
        return config;
    }

    private const string TAGS = "\"Tags\":[{\"Name\":\"T1\",\"Address\":\"40001\",\"DataType\":\"INTEGER\"}]";

    [Fact]
    public void 舊檔無選填欄位_DeviceName為null_通訊參數用預設()
    {
        var config = Map("{\"IP\":\"10.0.0.1\",\"Port\":502,\"ModbusId\":\"1,2\"," + TAGS + "}");

        Assert.Null(config.szDeviceName);
        Assert.Equal(2, config.nRetryCount);
        Assert.Equal(5, config.nNoResponseReconnectRounds);
        Assert.Equal(30000, config.nUnreachableProbeIntervalMs);
    }

    [Fact]
    public void 有DeviceName_含空字串皆保留()
    {
        Assert.Equal("甲,,丙", Map("{\"IP\":\"10.0.0.1\",\"ModbusId\":\"1,2,3\",\"DeviceName\":\"甲,,丙\"," + TAGS + "}").szDeviceName);
        Assert.Equal("", Map("{\"IP\":\"10.0.0.1\",\"ModbusId\":\"1\",\"DeviceName\":\"\"," + TAGS + "}").szDeviceName);
        Assert.Null(Map("{\"IP\":\"10.0.0.1\",\"ModbusId\":\"1\",\"DeviceName\":null," + TAGS + "}").szDeviceName);
    }

    [Fact]
    public void 通訊參數_覆寫與夾住範圍()
    {
        var config = Map("{\"IP\":\"10.0.0.1\",\"ModbusId\":\"1\",\"RetryCount\":9,\"NoResponseReconnectRounds\":0,\"UnreachableProbeIntervalMs\":-5," + TAGS + "}");

        Assert.Equal(5, config.nRetryCount);
        Assert.Equal(1, config.nNoResponseReconnectRounds);
        Assert.Equal(0, config.nUnreachableProbeIntervalMs);
    }
}
