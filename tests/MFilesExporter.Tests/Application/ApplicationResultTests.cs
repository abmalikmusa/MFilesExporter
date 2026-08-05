using FluentAssertions;
using MFilesExporter.Application.Common;

namespace MFilesExporter.Tests.Application;

public class ApplicationResultTests
{
    [Fact]
    public void Success_HasNoErrors()
    {
        var r = ApplicationResult.Success();
        r.IsSuccess.Should().BeTrue();
        r.IsFailure.Should().BeFalse();
        r.Errors.Should().BeEmpty();
        r.PrimaryError.Should().BeNull();
    }

    [Fact]
    public void Failure_CarriesErrors_AndReportsPrimary()
    {
        var err = ApplicationError.Validation("BAD", "boom");
        var r = ApplicationResult.Failure(err);
        r.IsFailure.Should().BeTrue();
        r.PrimaryError.Should().Be(err);
    }

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        var r = ApplicationResult<int>.Success(42);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
    }

    [Fact]
    public void GenericFailure_ReadingValue_Throws()
    {
        var r = ApplicationResult<int>.Failure(ApplicationError.NotFound("NF", "missing"));
        Action act = () => _ = r.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AsNonGeneric_PreservesFailure()
    {
        var r = ApplicationResult<int>.Failure(ApplicationError.Conflict("C", "state")).AsNonGeneric();
        r.IsFailure.Should().BeTrue();
        r.PrimaryError!.Code.Should().Be("C");
    }
}
