using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shop.Application.Abstractions;
using Shop.Application.Notifications;
using Shop.Infrastructure.DependencyInjection;

// The usual convention: extension methods for IServiceCollection live in its own namespace.
namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Every registration shape the dependency injection tool has to read, and one service nobody registers.</summary>
    public static class ServiceRegistration
    {
        public static IServiceCollection AddShopInfrastructure(this IServiceCollection services)
        {
            services.TryAddSingleton<IClock, SystemClock>();
            services.AddScoped(typeof(IRepository<>), typeof(InMemoryStore<>));
            services.AddKeyedSingleton<INotifier, SmsNotifier>("sms");
            services.AddTransient<IAuditSink>(_ => new FileAuditSink("audit.log"));
            services.AddTransient<AuditTrail>();

            // Distinct services that share a generic definition, and a generic helper that registers its type argument.
            services.AddSingleton(Channel.CreateUnbounded<string>().Reader);
            services.AddSingleton(Channel.CreateUnbounded<int>().Reader);
            AddJob<AuditFlushJob>();
            return services;

            void AddJob<TJob>()
                where TJob : class, IHostedService => services.AddHostedService<TJob>();
        }
    }
}

namespace Shop.Infrastructure.DependencyInjection
{
    public interface IClock
    {
        DateTimeOffset Now { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
    }

    public interface IAuditSink
    {
        void Write(string line);
    }

    public sealed class FileAuditSink(string path) : IAuditSink
    {
        public void Write(string line) => File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>Injected by <see cref="AuditTrail"/> and registered nowhere: resolving AuditTrail fails at run time.</summary>
    public interface IReportExporter
    {
        string Export(IEnumerable<string> lines);
    }

    public sealed class AuditTrail(IAuditSink sink, IClock clock, IReportExporter exporter)
    {
        public void Record(string what) => sink.Write($"{clock.Now:O} {what}");

        public string Export(IEnumerable<string> lines) => exporter.Export(lines);
    }

    public sealed class AuditFlushJob(AuditTrail trail) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            trail.Record("flush");
            return Task.CompletedTask;
        }
    }

    public sealed class InMemoryStore<T> : IRepository<T>
        where T : class
    {
        private readonly Dictionary<Guid, T> _items = [];

        public Task<T?> GetByIdAsync(Guid id) => Task.FromResult(_items.GetValueOrDefault(id));

        public Task SaveAsync(T entity) => Task.CompletedTask;
    }

    /// <summary>A switch per job, named like the job: a property and a type share a name.</summary>
    public sealed class JobSwitches
    {
        public bool AuditFlushJob { get; init; } = true;
    }
}
