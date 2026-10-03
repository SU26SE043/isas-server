using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

public class QuestionBankKContractQbk1Tests
{
    private static readonly Guid Campaign = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Candidate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PoolQuestion Q(string text, bool required = false, string? group = null)
        => new(Guid.NewGuid(), text, null, required, group);

    [Theory]
    [InlineData(0, 5, 3, 3, 3)]
    [InlineData(1, 4, 3, 3, 2)]
    [InlineData(1, 4, 4, 4, 3)]
    [InlineData(2, 3, 1, 2, 0)]
    [InlineData(2, 3, 9, 5, 3)]
    public void K_la_tong_so_cau_goc_theo_bang_QBK1_K5(
        int fixedCount, int poolCount, int k, int expectedReceived, int expectedDrawn)
    {
        var required = Enumerable.Range(1, fixedCount)
            .Select(i => Q($"Cố định {i}", required: true)).ToList();
        var pool = required.Concat(Enumerable.Range(1, poolCount)
            .Select(i => Q($"Trong rổ {i}"))).ToList();

        var result = QuestionPoolSelector.Select(pool, k, Campaign, Candidate);

        Assert.Equal(expectedReceived, result.Count);
        Assert.All(required, question => Assert.Contains(result, selected => selected.Id == question.Id));
        Assert.Equal(expectedDrawn, result.Count(question => !question.IsRequired));
    }

    [Fact]
    public void K_da_gom_cau_bat_buoc_khong_phai_so_cau_boc_them()
    {
        // FE commit 7cc1ddb9 từng hiểu K là số câu bốc thêm ngoài câu cố định.
        var required = Q("Cố định", required: true);
        var pool = new[] { required }
            .Concat(Enumerable.Range(1, 4).Select(i => Q($"Trong rổ {i}"))).ToList();

        var result = QuestionPoolSelector.Select(pool, 3, Campaign, Candidate);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, question => question.Id == required.Id);
        Assert.Equal(2, result.Count(question => !question.IsRequired));
    }
}
