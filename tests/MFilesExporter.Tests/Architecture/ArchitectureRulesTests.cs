using FluentAssertions;
using NetArchTest.Rules;

namespace MFilesExporter.Tests.Architecture;

/// <summary>
/// Structural invariants of the solution. If any of these fail, the layering
/// contract has been broken and must be restored before merge.
///
/// Rules (inward-only, no cycles):
///   Domain        depends on nothing project-owned.
///   Shared        depends on nothing project-owned.
///   Configuration depends only on Shared.
///   Logging       depends only on Shared.
///   Application   depends only on Domain, Configuration, Shared.
///   Persistence   depends only on Application/Domain/Configuration/Shared.
///   Export        depends only on Application/Domain/Configuration/Shared.
///   Reporting     depends only on Application/Domain/Configuration/Shared.
///   Infrastructure depends only on Application/Configuration/Logging/Shared.
///   Console (composition root) may reference everything.
/// </summary>
public class ArchitectureRulesTests
{
    private const string Domain = "MFilesExporter.Domain";
    private const string Shared = "MFilesExporter.Shared";
    private const string Configuration = "MFilesExporter.Configuration";
    private const string Logging = "MFilesExporter.Logging";
    private const string Application = "MFilesExporter.Application";
    private const string Persistence = "MFilesExporter.Persistence";
    private const string Export = "MFilesExporter.Export";
    private const string Reporting = "MFilesExporter.Reporting";
    private const string Infrastructure = "MFilesExporter.Infrastructure";
    private const string Console = "MFilesExporter.Console";

    [Fact]
    public void Domain_HasNoProjectDependencies()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Domain.Documents.IdempotencyKey).Assembly)
            .Should().NotHaveDependencyOnAny(
                Shared, Configuration, Logging,
                Application, Persistence, Export, Reporting, Infrastructure, Console)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain must remain pure — but referenced: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Application_DoesNotDependOn_PersistenceExportReportingInfrastructure()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Application.UseCases.ExportOrchestrator).Assembly)
            .Should().NotHaveDependencyOnAny(
                Persistence, Export, Reporting, Infrastructure, Console, Logging)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application must not reference outer layers. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Persistence_DoesNotDependOn_ExportReportingInfrastructureConsole()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Persistence.State.SqliteStateStore).Assembly)
            .Should().NotHaveDependencyOnAny(Export, Reporting, Infrastructure, Console)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Persistence must not reference peer adapter layers. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Export_DoesNotDependOn_PersistenceReportingInfrastructureConsole()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Export.Pipeline.ExportPipeline).Assembly)
            .Should().NotHaveDependencyOnAny(Persistence, Reporting, Infrastructure, Console)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Export must not reference peer adapter layers. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Reporting_DoesNotDependOn_PersistenceExportInfrastructureConsole()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Reporting.LoggingProgressReporter).Assembly)
            .Should().NotHaveDependencyOnAny(Persistence, Export, Infrastructure, Console)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Reporting must not reference peer adapter layers. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Infrastructure_DoesNotDependOn_PersistenceExportReportingConsole()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Infrastructure.Time.SystemClock).Assembly)
            .Should().NotHaveDependencyOnAny(Persistence, Export, Reporting, Console)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Infrastructure must not reference peer adapter layers. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Configuration_DoesNotDependOn_LayersAbove()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Configuration.Options.ExporterOptions).Assembly)
            .Should().NotHaveDependencyOnAny(
                Application, Persistence, Export, Reporting, Infrastructure, Console, Logging, Domain)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Configuration is a leaf project. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void Logging_DoesNotDependOn_BusinessProjects()
    {
        var result = Types.InAssembly(typeof(global::MFilesExporter.Logging.SerilogBootstrap).Assembly)
            .Should().NotHaveDependencyOnAny(
                Application, Persistence, Export, Reporting, Infrastructure, Console, Domain)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Logging is a leaf project. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }
}
