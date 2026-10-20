using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Packory;
using Xunit;

namespace Packory.Tests;

internal static class SkillScanTestServices
{
    public static void DisableBackgroundSkillScanning(this IServiceCollection services)
    {
        // Manual scan fixtures own QUEUED -> review/approval transitions. Keep the
        // concrete coordinator for real approval endpoints and security enforcement,
        // but never let its timer race those deliberately synthetic scan results.
        var registration = Assert.Single(services, service =>
            !service.IsKeyedService && service.ServiceType == typeof(IHostedService) &&
            (service.ImplementationType == typeof(SkillScanCoordinator) ||
             service.ImplementationInstance is SkillScanCoordinator ||
             service.ImplementationFactory is Func<IServiceProvider, SkillScanCoordinator>));
        services.Remove(registration);
    }
}
