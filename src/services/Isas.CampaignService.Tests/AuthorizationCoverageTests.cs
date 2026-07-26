using System.Reflection;
using Isas.CampaignService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Isas.CampaignService.Tests;

/// <summary>
/// A5 (AUTH-3/6/7) — guard tĩnh: KHÔNG endpoint nào "trần". + assert: CampaignController (Employer)
/// mọi action Roles="Employer"; metadata magic-link = public; join/my-campaigns/start = Candidate.
/// </summary>
public class AuthorizationCoverageTests
{
    private static readonly Assembly ServiceAssembly = typeof(CampaignController).Assembly;

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

    [Fact]
    public void CampaignController_AllActions_RequireEmployerRole()
    {
        var bad = ActionMethods(typeof(CampaignController))
            .Where(a => a.GetCustomAttribute<AuthorizeAttribute>()?.Roles != "Employer")
            .Select(a => a.Name)
            .ToList();

        Assert.True(bad.Count == 0, "Action Campaign thiếu Roles=Employer: " + string.Join(", ", bad));
    }

    [Theory]
    [InlineData(nameof(ParticipationController.GetInvitation))]
    public void ParticipationMagicLink_IsPublic(string method)
    {
        var m = typeof(ParticipationController).GetMethod(method)!;
        Assert.NotNull(m.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData(nameof(ParticipationController.JoinCampaign))]
    [InlineData(nameof(ParticipationController.GetMyCampaigns))]
    [InlineData(nameof(ParticipationController.StartInterview))]
    public void ParticipationCandidateEndpoints_RequireCandidateRole(string method)
    {
        var attr = typeof(ParticipationController).GetMethod(method)!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("Candidate", attr!.Roles);
    }
}
