using Isas.AuthService.DTOs;
using Isas.AuthService.Models;
using Isas.AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Isas.AuthService.Tests;

/// <summary>
/// Nguyên tử hoá các đường TẠO TÀI KHOẢN: hoặc tài khoản ra đời ĐẦY ĐỦ (user + role + membership
/// nếu có), hoặc KHÔNG CÓ GÌ.
///
/// Trước đây mỗi đường chạy 3–5 lần <c>SaveChanges</c> rời rạc, lỗi giữa chừng để lại tài khoản dở
/// dang mà không có gì dọn: user không role thì đăng nhập được nhưng mọi endpoint
/// <c>[Authorize(Roles)]</c> trả 403; Employer không thuộc org nào thì mất cả quyền lẫn billing
/// (AUTH-8); mà email đã UNIQUE nên KHÔNG đăng ký / mời lại được để tự sửa.
///
/// Cách bơm lỗi: <c>UserManager</c> mock persist user thật vào SQLite ở <c>CreateAsync</c> (đúng như
/// production, nơi Identity dùng CHUNG <c>AuthDbContext</c>), rồi bước SAU đó được cho hỏng. Kiểm
/// bằng context MỚI để chắc chắn đọc từ DB chứ không từ change tracker.
/// </summary>
public class AccountCreationTransactionTests
{
    // ---------- RegisterAsync (AUTH-1) ----------

    [Fact]
    public async Task Register_LoiGanRole_KhongDeLaiUserMoCoi()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        FailAddToRole(userManager);
        var sut = NewService(db, userManager);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        }));

        // Không có user mồ côi: user đã INSERT xong mới tới bước role hỏng → phải bị cuốn theo.
        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
        Assert.Empty(verify.RefreshTokens);
    }

    [Fact]
    public async Task Register_ThanhCong_TaoDuUserVaRole()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        var resp = await sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        });

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));

        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);
        Assert.Single(verify.RefreshTokens);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Candidate"), Times.Once);
    }

    /// <summary>
    /// <c>AddToRoleAsync</c> có thể BÁO HỎNG thay vì ném. Trước đây kết quả bị bỏ qua hoàn toàn ⇒
    /// user không role được commit trong im lặng, không ai biết. Transaction không tự cứu được ca
    /// này — phải đọc kết quả rồi mới ném.
    /// </summary>
    [Fact]
    public async Task Register_GanRoleTraVeThatBaiKhongNem_VanKhongTaoUser()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        userManager.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "role store down" }));
        var sut = NewService(db, userManager);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        }));

        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
    }

    // ---------- RegisterOrgAsync (A3 / AUTH-8) ----------

    [Fact]
    public async Task RegisterOrg_LoiTaoOrg_KhongDeLaiEmployerThieuOrg()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        // OrgName null → vi phạm NOT NULL của organizations.name khi SaveChanges. Lỗi rơi vào ĐÚNG
        // bước tạo org, tức là SAU khi user + role đã ghi.
        await Assert.ThrowsAnyAsync<Exception>(() => sut.RegisterOrgAsync(new RegisterOrgRequest
        {
            Email = "boss@acme.test",
            Password = "Passw0rd!",
            FullName = "Boss",
            OrgName = null!
        }));

        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);          // không còn Employer thiếu org
        Assert.Empty(verify.Organizations);
        Assert.Empty(verify.OrgMembers);
    }

    [Fact]
    public async Task RegisterOrg_LoiGanRole_KhongDeLaiUserMoCoi()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        FailAddToRole(userManager);
        var sut = NewService(db, userManager);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.RegisterOrgAsync(new RegisterOrgRequest
        {
            Email = "boss@acme.test",
            Password = "Passw0rd!",
            FullName = "Boss",
            OrgName = "Acme Corp"
        }));

        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
        Assert.Empty(verify.Organizations);
        Assert.Empty(verify.OrgMembers);
    }

    [Fact]
    public async Task RegisterOrg_ThanhCong_TaoDuUserOrgVaMembership()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        var resp = await sut.RegisterOrgAsync(new RegisterOrgRequest
        {
            Email = "boss@acme.test",
            Password = "Passw0rd!",
            FullName = "Boss",
            OrgName = "Acme Corp"
        });

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));

        using var verify = testDb.NewContext();
        var user = Assert.Single(verify.Users);
        var org = Assert.Single(verify.Organizations);
        var member = Assert.Single(verify.OrgMembers);
        Assert.Equal("Acme Corp", org.Name);
        Assert.Equal(org.Id, member.OrgId);
        Assert.Equal(user.Id, member.UserId);
        Assert.Equal(OrgRole.OrgAdmin, member.OrgRole);
    }

    // ---------- ProvisionCandidateAsync (D2 magic-link) ----------

    [Fact]
    public async Task ProvisionCandidate_LoiGanRole_KhongDeLaiUserMoCoi()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        FailAddToRole(userManager);
        var sut = NewService(db, userManager);

        await Assert.ThrowsAnyAsync<Exception>(
            () => sut.ProvisionCandidateAsync("invitee@acme.test", "Invitee"));

        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
    }

    [Fact]
    public async Task ProvisionCandidate_ThanhCong_TaoDuUserVaRole()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        var resp = await sut.ProvisionCandidateAsync("invitee@acme.test", "Invitee");

        Assert.NotEqual(Guid.Empty, resp.CandidateId);
        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);
        userManager.Verify(m => m.AddToRoleAsync(It.IsAny<User>(), "Candidate"), Times.Once);
    }

    // ---------- AddOrgMemberAsync (A6) ----------

    [Fact]
    public async Task AddOrgMember_LoiGanRole_KhongDeLaiUserMoCoiVaKhongMembership()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme Corp");
        var userManager = MockUserManager(db);
        FailAddToRole(userManager);
        var sut = NewService(db, userManager);

        await Assert.ThrowsAnyAsync<Exception>(
            () => sut.AddOrgMemberAsync(orgId, "hr@acme.test", "HR"));

        // Quan trọng: email UNIQUE ⇒ user mồ côi sót lại sẽ khiến OrgAdmin KHÔNG mời lại được
        // chính email đó (409 "đã đăng ký"), tức là hỏng vĩnh viễn cho email đó.
        using var verify = testDb.NewContext();
        Assert.Empty(verify.Users);
        Assert.Empty(verify.OrgMembers);
    }

    [Fact]
    public async Task AddOrgMember_ThanhCong_TaoDuUserVaMembership()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var orgId = SeedOrg(db, "Acme Corp");
        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        var resp = await sut.AddOrgMemberAsync(orgId, "hr@acme.test", "HR");

        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);
        var member = Assert.Single(verify.OrgMembers);
        Assert.Equal(orgId, member.OrgId);
        Assert.Equal(resp.UserId, member.UserId);
        Assert.Equal(OrgRole.HrMember, member.OrgRole);
    }

    // ---------- EnsureRoleExistsAsync: đua tạo role lần đầu ----------

    /// <summary>
    /// Hai request đăng ký ĐẦU TIÊN cùng lúc sẽ đua nhau tạo role; bên thua đụng UNIQUE
    /// <c>RoleNameIndex</c>. Bên thua vẫn phải đăng ký được BÌNH THƯỜNG — mục tiêu "role tồn tại"
    /// đã đạt, ai tạo không quan trọng. Đây là lý do <c>EnsureRoleExistsAsync</c> nằm NGOÀI
    /// transaction: ở trong thì lỗi này sẽ cuốn theo cả việc tạo user.
    /// </summary>
    [Fact]
    public async Task Register_ThuaDuaTaoRole_VanDangKyThanhCong()
    {
        using var testDb = new AuthTestDb();
        var db = testDb.Db;
        var userManager = MockUserManager(db);

        // Lượt kiểm đầu: chưa có role → thử tạo → đụng UNIQUE (request khác vừa tạo xong).
        // Lượt kiểm sau: role đã có → đi tiếp.
        var roleManager = MockRoleManager();
        var checkCount = 0;
        roleManager.Setup(m => m.RoleExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(() => checkCount++ > 0);
        // Mô phỏng ĐÚNG RoleStore thật: Add vào context RỒI SaveChanges mới ném → entity Added còn
        // KẸT trong change tracker sau khi ném. Không gỡ ra thì SaveChanges kế tiếp (nằm trong
        // transaction tạo user) sẽ lôi nó theo.
        roleManager.Setup(m => m.CreateAsync(It.IsAny<Role>()))
            .Returns<Role>(r =>
            {
                db.Roles.Add(r);
                throw new DbUpdateException("duplicate key value violates unique constraint \"RoleNameIndex\"");
            });

        var sut = new Isas.AuthService.Services.AuthService(
            db, new JwtService(TestConfig()), userManager.Object, roleManager.Object,
            TestConfig(), MockSignInManager(userManager.Object).Object);

        var resp = await sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        });

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        using var verify = testDb.NewContext();
        Assert.Single(verify.Users);
        // Role hỏng KHÔNG được kẹt lại trong change tracker rồi làm hỏng lây SaveChanges kế tiếp.
        Assert.Empty(verify.Roles);
    }

    /// <summary>
    /// Hàng rào chống một lỗi CHỈ NỔ TRÊN PRODUCTION: Program.cs bật <c>EnableRetryOnFailure()</c>
    /// trên Npgsql, và chiến lược retry của EF TỪ CHỐI transaction do người dùng tự mở. Viết
    /// <c>BeginTransactionAsync</c> thẳng (không qua <c>CreateExecutionStrategy()</c>) sẽ ném ở MỌI
    /// lần đăng ký trên Postgres, trong khi test SQLite mặc định (chiến lược không-retry) vẫn xanh
    /// 100% — đúng kiểu bug DB25b.
    ///
    /// Nên test này cấu hình SQLite bằng một chiến lược CÓ retry để tái hiện đúng ràng buộc đó:
    /// code sai sẽ ĐỎ ngay tại đây thay vì lúc deploy.
    /// </summary>
    [Fact]
    public async Task TaoTaiKhoan_ChayDuocDuoiChienLuocCoRetry()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(conn, o => o.ExecutionStrategy(deps => new RetryingStrategyStub(deps)))
            .Options;

        using var db = new AuthDbContext(options);
        db.Database.EnsureCreated();

        var userManager = MockUserManager(db);
        var sut = NewService(db, userManager);

        var resp = await sut.RegisterAsync(new RegisterRequest
        {
            Email = "candidate@acme.test",
            Password = "Passw0rd!",
            FullName = "Candidate"
        });

        Assert.False(string.IsNullOrWhiteSpace(resp.AccessToken));
        Assert.Equal(1, db.Users.Count());
    }

    /// <summary>Chiến lược có <c>RetriesOnFailure = true</c> nhưng KHÔNG thử lại thật — chỉ để bật
    /// đúng ràng buộc "không cho tự mở transaction" của EF.</summary>
    private sealed class RetryingStrategyStub(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        public override bool RetriesOnFailure => true;
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    // ---------- helpers ----------

    private static void FailAddToRole(Mock<UserManager<User>> userManager) =>
        userManager.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("role store unavailable"));

    private static Guid SeedOrg(AuthDbContext db, string name)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
        db.Organizations.Add(org);
        db.SaveChanges();
        return org.Id;
    }

    private static Isas.AuthService.Services.AuthService NewService(
        AuthDbContext db, Mock<UserManager<User>> userManager)
    {
        var config = TestConfig();
        return new Isas.AuthService.Services.AuthService(
            db, new JwtService(config), userManager.Object, MockRoleManager().Object,
            config, MockSignInManager(userManager.Object).Object);
    }

    private static IConfiguration TestConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "isas-test-signing-key-0123456789-abcdef",
            ["Jwt:Issuer"] = "isas-test",
            ["Jwt:Audience"] = "isas-test",
            ["Jwt:AccessTokenMinutes"] = "15",
            ["Jwt:RefreshTokenDays"] = "7"
        }).Build();

    private static Mock<UserManager<User>> MockUserManager(AuthDbContext db)
    {
        var mgr = new Mock<UserManager<User>>(
            Mock.Of<IUserStore<User>>(), null!, null!, null!, null!, null!, null!, null!, null!);

        mgr.Setup(m => m.FindByEmailAsync(It.IsAny<string>()))
            .Returns<string>(e => Task.FromResult(
                db.Users.FirstOrDefault(u => u.Email!.ToLower() == e.ToLower())));

        // Giống production: Identity ghi qua CHÍNH AuthDbContext này ⇒ nằm trong transaction đang mở.
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Returns<User, string>((u, _) =>
            {
                db.Users.Add(u);
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });
        mgr.Setup(m => m.CreateAsync(It.IsAny<User>()))
            .Returns<User>(u =>
            {
                db.Users.Add(u);
                db.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });
        mgr.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        mgr.Setup(m => m.GetRolesAsync(It.IsAny<User>()))
            .ReturnsAsync(new List<string> { "Candidate" });
        return mgr;
    }

    private static Mock<RoleManager<Role>> MockRoleManager()
    {
        var mgr = new Mock<RoleManager<Role>>(Mock.Of<IRoleStore<Role>>(), null!, null!, null!, null!);
        mgr.Setup(m => m.RoleExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        return mgr;
    }

    private static Mock<SignInManager<User>> MockSignInManager(UserManager<User> userManager) =>
        new(userManager, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(), null!, null!, null!, null!);
}
