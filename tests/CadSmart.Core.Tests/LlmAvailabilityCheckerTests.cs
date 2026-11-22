using Xunit;
using CadSmart.Core.Providers;
using CadSmart.Core.Setup;

namespace CadSmart.Tests;

/// <summary>
/// 本地智能可用性检查与安装引导（路线A）测试。
/// </summary>
public class LlmAvailabilityCheckerTests
{
    [Fact]
    public async Task 本地运行时就绪_返回local模式与运行时名()
    {
        var local = new LocalLlmProvider(
            new HttpClient(),
            new LlmOptions { Endpoint = "http://127.0.0.1:11434/v1/chat/completions", Model = "qwen" },
            ports: [11434]);
        var probe = await local.ProbeAsync();
        if (!probe)
        {
            // 本机没装 Ollama：验证 fallback 分支（纯规则 + 引导）
            var avail0 = LlmAvailabilityChecker.Check(local, cloudConfigured: false);
            Assert.Equal("none", avail0.Mode);
            Assert.NotEmpty(avail0.SetupOptions);
            return;
        }

        var ok = LlmAvailabilityChecker.Check(local, cloudConfigured: false);
        Assert.Equal("local", ok.Mode);
        Assert.True(ok.IsEnhanced);
        Assert.NotNull(ok.LocalPort);
    }

    [Fact]
    public void 云端已配置_返回cloud模式()
    {
        var cloud = new CloudLlmProvider(
            new HttpClient(),
            new LlmOptions { Endpoint = "https://api.test/v1/chat/completions", Model = "m", ApiKey = "k" });
        var avail = LlmAvailabilityChecker.Check(cloud, cloudConfigured: true);
        Assert.Equal("cloud", avail.Mode);
        Assert.True(avail.IsEnhanced);
        Assert.Contains("云端", avail.UserMessage);
    }

    [Fact]
    public void Mock供应商_返回开发模式()
    {
        var avail = LlmAvailabilityChecker.Check(new MockLlmProvider(), cloudConfigured: false);
        Assert.Equal("mock", avail.Mode);
    }

    [Fact]
    public void 全无_返回纯规则并给出三个安装引导()
    {
        var avail = LlmAvailabilityChecker.Check(new NullLlmProvider(), cloudConfigured: false);
        Assert.Equal("none", avail.Mode);
        Assert.False(avail.IsEnhanced);
        Assert.Contains("纯规则", avail.UserMessage);
        Assert.Equal(3, avail.SetupOptions.Count);

        var first = avail.SetupOptions[0];
        Assert.Equal("LocModel", first.Name);
        Assert.True(first.IsRecommended);
        Assert.StartsWith("https://locmodel.com", first.DownloadUrl);
        Assert.Contains("Qwen", first.ModelSuggestion);
    }

    [Fact]
    public void 引导清单_包含三个运行时且端口正确()
    {
        var opts = LlmAvailabilityChecker.SetupOptions;
        Assert.Contains(opts, o => o.Name == "LocModel" && o.IsRecommended);
        Assert.Contains(opts, o => o.Name == "Ollama" && o.Port.Contains("11434"));
        Assert.Contains(opts, o => o.Name == "LM Studio" && o.Port.Contains("1234"));
    }
}
