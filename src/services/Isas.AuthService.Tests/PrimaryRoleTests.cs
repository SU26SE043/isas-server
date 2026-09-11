using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// User có NHIỀU platform-role (tài khoản admin trên dev/prod: register → Candidate, rồi INSERT Admin bằng SQL).
/// Trước đây <c>/auth/me</c>, <c>ListAllUsers</c>, <c>ToAdminUserResponse</c> đều lấy <c>.FirstOrDefault()</c>
/// theo thứ tự DB trả về — đo trên dev: admin@ nhận <c>role: "Candidate"</c> ⇒ FE chặn khỏi mọi trang admin.
/// ⚠ Mock ở đây trả roles theo ĐÚNG thứ tự đưa vào (KHÔNG sort như <c>RoleAwareUserManager</c> — sort theo tên
/// đặt "Admin" lên đầu và làm test xanh ngay cả với <c>.FirstOrDefault()</c>).
/// </summary>
public class PrimaryRoleTests
{
    [Theory]
    [InlineData(new[] { "Candidate", "Admin" }, "Admin")]
    [InlineData(new[] { "Candidate", "Employer" }, "Employer")]
    [InlineData(new[] { "Employer", "Admin", "Candidate" }, "Admin")]
    [InlineData(new[] { "Candidate" }, "Candidate")]
    [InlineData(new[] { "Something" }, "Something")]
    [InlineData(new string[0], "No role")]
    public void PrimaryRole_ChonTheoMucDacQuyen_KhongTheoThuTuDb(string[] roles, string expected)
    {
        Assert.Equal(expected, Isas.AuthService.Services.AuthService.PrimaryRole(roles));
    }

    [Fact]
    public async Task GetUser_AdminCoThemCandidate_TraRoleAdmin()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = new User { Id = Guid.NewGuid(), UserName = "admin@isas.local", Email = "admin@isas.local", FullName = "Admin", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        db.SaveChanges();

        // Thứ tự "Candidate" trước "Admin" = đúng thứ tự Postgres trên dev đã trả về.
        var sut = NewService(db, ["Candidate", "Admin"]);
        var me = await sut.GetUserAsync(user.Id);

        Assert.Equal("Admin", me.Role);
    }

    [Fact]
    public async Task ListAllUsers_AdminCoThemCandidate_HangHienThiRoleAdmin()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var user = new User { Id = Guid.NewGuid(), UserName = "admin@isas.local", Email = "admin@isas.local", FullName = "Admin", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        db.SaveChanges();

        var sut = NewService(db, ["Candidate", "Admin"]);
        var page = await sut.ListAllUsersAsync(role: null, search: null, cursor: null, limit: null);

        Assert.Equal("Admin", Assert.Single(page.Items).Role);
    }

    private static Isas.AuthService.Services.AuthService NewService(AuthDbContext db, string[] rolesInDbOrder)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7"
        }).Build();
        var userManager = new Mock<UserManager<User>>(Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync((IList<string>)rolesInDbOrder.ToList());
        var roleManager = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        var signInManager = new Mock<SignInManager<User>>(userManager.Object, Mock.Of<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
        return new Isas.AuthService.Services.AuthService(db, new JwtService(config), userManager.Object, roleManager.Object, config, signInManager.Object);
    }
}
