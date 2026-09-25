using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PanRaProxy.Diagnostics;
using PanRaProxy.Mappings;
using PanRaProxy.Options;

namespace PanRaProxy.Tests;

/// <summary>
/// A <see cref="ProxyMetrics"/> on its own meter factory, with collectors to read what was recorded.
/// </summary>
public sealed class TestMetrics : IDisposable
{
    private readonly ServiceProvider services;
    private readonly IMeterFactory meterFactory;

    public TestMetrics(MappingBatcher? batcher = null, AccountingRequestQueue? requests = null)
    {
        this.services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        this.meterFactory = this.services.GetRequiredService<IMeterFactory>();
        this.Batcher = batcher ?? new MappingBatcher(Microsoft.Extensions.Options.Options.Create(new UserIdOptions()), TimeProvider.System);
        this.Requests = requests ?? new AccountingRequestQueue(Microsoft.Extensions.Options.Options.Create(new UserIdOptions()));
        this.Metrics = new ProxyMetrics(this.meterFactory, this.Batcher, this.Requests);
    }

    public ProxyMetrics Metrics { get; }

    public MappingBatcher Batcher { get; }

    public AccountingRequestQueue Requests { get; }

    public MetricCollector<long> Collect(string instrument) => new(this.meterFactory, ProxyMetrics.MeterName, instrument);

    public MetricCollector<int> CollectInt(string instrument) => new(this.meterFactory, ProxyMetrics.MeterName, instrument);

    public void Dispose() => this.services.Dispose();
}
