using Isas.PaymentService.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Isas.PaymentService.Tests;

public class PlanControllerTests
{
    [Fact]
    public void Controller_IsAdminOnly()
    {
        var authorization = typeof(PlanController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Equal("Admin", authorization.Roles);
    }
}
