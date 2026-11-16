using DepSweep.Core.Models;
using DepSweep.Core.PackageManagers;

namespace DepSweep.Core.Tests;

public class AuditParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Npm_vulnerable_report_is_parsed_with_safe_fixes()
    {
        var result = NpmAuditParser.Parse(Fixture("npm-audit-vulnerable.json"));

        result.IsClean.ShouldBeFalse();
        result.Vulnerabilities.Select(v => v.Package).ShouldBe(["minimist", "lodash"], ignoreOrder: true);

        var lodash = result.Vulnerabilities.Single(v => v.Package == "lodash");
        lodash.IsDirect.ShouldBeTrue();
        lodash.Fix.ShouldBe(FixKind.Fixable);
        lodash.FixVersion.ShouldNotBeNull().ShouldStartWith("lodash@");
        lodash.Advisories.ShouldNotBeEmpty();
        lodash.Advisories.ShouldAllBe(a => a.Url != null && a.Url.StartsWith("https://"));

        result.Counts.Total.ShouldBe(2);
        result.Vulnerabilities[0].Severity.ShouldBe(Severity.Critical, "sorted most severe first");
    }

    [Fact]
    public void Npm_report_distinguishes_force_and_no_fix()
    {
        var result = NpmAuditParser.Parse(Fixture("npm-audit-needs-force.json"));

        result.Vulnerabilities.Single(v => v.Package == "jsonwebtoken").Fix.ShouldBe(FixKind.NeedsForce);
        result.Vulnerabilities.Single(v => v.Package == "jsonwebtoken").FixVersion.ShouldNotBeNull().ShouldStartWith("jsonwebtoken@9");
        result.Vulnerabilities.Single(v => v.Package == "request").Fix.ShouldBe(FixKind.NoFix);
        result.Vulnerabilities.Single(v => v.Package == "axios").Fix.ShouldBe(FixKind.Fixable);
        result.Vulnerabilities.Single(v => v.Package == "form-data").IsDirect.ShouldBeFalse();
        result.Counts.ShouldBe(new SeverityCounts(0, 0, 3, 2, 2));
    }

    [Fact]
    public void Npm_clean_report_is_clean()
    {
        var result = NpmAuditParser.Parse(Fixture("npm-audit-clean.json"));
        result.IsClean.ShouldBeTrue();
        result.Vulnerabilities.ShouldBeEmpty();
    }

    [Fact]
    public void Npm_error_payload_throws_with_message()
    {
        var ex = Should.Throw<AuditFailedException>(() => NpmAuditParser.Parse(Fixture("npm-audit-error.json")));
        ex.Message.ShouldContain("ECONNREFUSED");
    }

    [Theory]
    [InlineData("")]
    [InlineData("npm ERR! something went wrong")]
    [InlineData("{ not json")]
    public void Npm_garbage_output_throws(string output) =>
        Should.Throw<AuditFailedException>(() => NpmAuditParser.Parse(output));

    [Fact]
    public void Npm_leading_warning_text_is_ignored()
    {
        var result = NpmAuditParser.Parse("npm warn config something\n" + Fixture("npm-audit-clean.json"));
        result.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void Pnpm_vulnerable_report_groups_advisories_by_package()
    {
        var result = PnpmAuditParser.Parse(Fixture("pnpm-audit-vulnerable.json"));

        result.Counts.ShouldBe(new SeverityCounts(0, 0, 3, 3, 1));
        result.Vulnerabilities.Select(v => v.Package).ShouldBe(["minimist", "lodash"], ignoreOrder: true);

        var minimist = result.Vulnerabilities.Single(v => v.Package == "minimist");
        minimist.Severity.ShouldBe(Severity.Critical);
        minimist.IsDirect.ShouldBeTrue();
        minimist.Fix.ShouldBe(FixKind.Fixable);
        minimist.Advisories.Count.ShouldBe(2);
    }

    [Fact]
    public void Exactly_pinned_direct_dependencies_need_force()
    {
        var declared = new Dictionary<string, string> { ["lodash"] = "4.17.15", ["minimist"] = "^1.2.0" };

        var npm = NpmAuditParser.Parse(Fixture("npm-audit-vulnerable.json"), declared);
        npm.Vulnerabilities.Single(v => v.Package == "lodash").Fix.ShouldBe(FixKind.NeedsForce);
        npm.Vulnerabilities.Single(v => v.Package == "minimist").Fix.ShouldBe(FixKind.Fixable);

        var pnpm = PnpmAuditParser.Parse(Fixture("pnpm-audit-vulnerable.json"), declared);
        pnpm.Vulnerabilities.Single(v => v.Package == "lodash").Fix.ShouldBe(FixKind.NeedsForce);
        pnpm.Vulnerabilities.Single(v => v.Package == "minimist").Fix.ShouldBe(FixKind.Fixable);
    }

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("=1.2.0", true)]
    [InlineData("1.2.0-beta.1", true)]
    [InlineData("^1.2.0", false)]
    [InlineData("~1.2.0", false)]
    [InlineData(">=1.2.0", false)]
    [InlineData("1.x", false)]
    [InlineData("*", false)]
    [InlineData("latest", false)]
    [InlineData("github:user/repo", false)]
    public void Exact_version_detection(string spec, bool exact) => PackageJson.IsExactVersion(spec).ShouldBe(exact);

    [Fact]
    public void Pnpm_clean_report_is_clean() =>
        PnpmAuditParser.Parse(Fixture("pnpm-audit-clean.json")).IsClean.ShouldBeTrue();

    [Fact]
    public void Pnpm_error_payload_throws() =>
        Should.Throw<AuditFailedException>(() =>
            PnpmAuditParser.Parse("""{"error":{"code":"ERR_PNPM_AUDIT_BAD_RESPONSE","message":"The audit endpoint responded with 500"}}"""))
        .Message.ShouldContain("ERR_PNPM_AUDIT_BAD_RESPONSE");
}
