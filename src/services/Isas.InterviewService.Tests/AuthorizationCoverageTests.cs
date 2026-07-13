using System.Reflection;
using Isas.InterviewService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.InterviewService.Tests;

/// <summary>
/// A5 (AUTH-3/6/7) — guard tĩnh: KHÔNG endpoint nào "trần". + assert: các controller B2C
/// (Practice/Roadmaps/CvAnalysis/Answers/Files) = Roles="Candidate"; 2 callback internal của Answers
/// (result/failed, X-Internal-Token) = [AllowAnonymous].
/// </summary>
public class AuthorizationCoverageTests
{
    private static readonly Assembly ServiceAssembly = typeof(PracticeController).Assembly;

    private static IEnumerable<Type> ControllerTypes() =>
        ServiceAssembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

    private static IEnumerable<MethodInfo> ActionMethods(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName
                        && m.GetBaseDefinition().DeclaringType == m.DeclaringType
                        && m.GetCustomAttribute<NonActionAttribute>() is null);

    private static bool HasClassAuthz(Type t) =>
        t.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
        || t.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    private static bool HasMethodAuthz(MethodInfo m) =>
        m.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
        || m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

    [Fact]
    public void EveryAction_IsCoveredBy_AuthorizeOrAllowAnonymous()
    {
        var naked = new List<string>();
        foreach (var c in ControllerTypes())
        {
            var classCovered = HasClassAuthz(c);
            foreach (var a in ActionMethods(c))
                if (!classCovered && !HasMethodAuthz(a))
                    naked.Add($"{c.Name}.{a.Name}");
        }

        Assert.True(naked.Count == 0,
            "Action 'trần' (thiếu cả [Authorize] lẫn [AllowAnonymous]): " + string.Join(", ", naked));
    }

    [Theory]
    [InlineData(typeof(PracticeController))]
    [InlineData(typeof(RoadmapsController))]
    [InlineData(typeof(CvAnalysisController))]
    [InlineData(typeof(AnswersController))]
    [InlineData(typeof(InterviewController))]
    public void B2CControllers_RequireCandidateRole(Type controller)
    {
        var attr = controller.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Candidate", attr!.Roles);
    }

    [Theory]
    [InlineData(nameof(AnswersController.SaveResult))]
    [InlineData(nameof(AnswersController.MarkFailed))]
    public void AnswersInternalCallbacks_AreAnonymous(string method)
    {
        var m = typeof(AnswersController).GetMethod(method)!;
        Assert.NotNull(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
