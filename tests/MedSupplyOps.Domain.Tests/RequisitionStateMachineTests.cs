using MedSupplyOps.Domain.Requisitions;

namespace MedSupplyOps.Domain.Tests;

/// <summary>
/// 請領單狀態機（FR-304）的測試。
///
/// 核心手法：窮舉 (狀態 × 動作) 的完整笛卡兒積，斷言「恰好這 5 組成立、其餘 25 組全部被拒」。
/// 抽樣式的測試（只挑幾條合法路徑來測）會漏掉「有人偷偷多開一條非法轉換」的情況 ——
/// 而多開的那一條不會讓任何既有測試變紅，正是典型的「錯了但看起來完全正常」。
/// </summary>
public sealed class RequisitionStateMachineTests
{
    private static readonly (RequisitionStatus From, RequisitionAction Action, RequisitionStatus To)[] Expected =
    [
        (RequisitionStatus.Draft, RequisitionAction.Submit, RequisitionStatus.PendingApproval),
        (RequisitionStatus.PendingApproval, RequisitionAction.Approve, RequisitionStatus.Approved),
        (RequisitionStatus.PendingApproval, RequisitionAction.Reject, RequisitionStatus.Rejected),
        (RequisitionStatus.Approved, RequisitionAction.Issue, RequisitionStatus.Issued),
        (RequisitionStatus.Issued, RequisitionAction.Close, RequisitionStatus.Closed),
    ];

    [Fact]
    public void ExactlyFiveTransitionsAreAllowed_AndTheyAreTheExpectedOnes()
    {
        var statuses = Enum.GetValues<RequisitionStatus>();
        var actions = Enum.GetValues<RequisitionAction>();

        var actuallyAllowed = new List<(RequisitionStatus From, RequisitionAction Action, RequisitionStatus To)>();

        foreach (var status in statuses)
        {
            foreach (var action in actions)
            {
                if (RequisitionStateMachine.TryTransition(status, action, out var to))
                {
                    actuallyAllowed.Add((status, action, to));
                }
            }
        }

        // 釘住「總數」：多開一條路，這行就紅。
        Assert.Equal(Expected.Length, actuallyAllowed.Count);
        Assert.Equal(Expected.Length, RequisitionStateMachine.AllowedTransitionCount);

        // 釘住「內容」：換掉其中一條路（總數不變），這行就紅。
        Assert.Equal(
            Expected.OrderBy(t => t.From).ThenBy(t => t.Action),
            actuallyAllowed.OrderBy(t => t.From).ThenBy(t => t.Action));
    }

    [Fact]
    public void CartesianProductSizeIsPinned_SoTheExhaustiveTestStaysExhaustive()
    {
        // 這條在測「上一條測試的母體對不對」。
        // 日後若有人新增狀態或動作卻忘了更新 Expected，這條會先提醒母體變大了 ——
        // 否則窮舉測試會在不知不覺中變成抽樣測試。
        var statusCount = Enum.GetValues<RequisitionStatus>().Length;
        var actionCount = Enum.GetValues<RequisitionAction>().Length;

        Assert.Equal(6, statusCount);
        Assert.Equal(5, actionCount);
        Assert.Equal(30, statusCount * actionCount);
    }

    [Theory]
    [MemberData(nameof(AllowedCases))]
    public void AllowedTransition_ProducesExpectedTargetStatus(
        RequisitionStatus from, RequisitionAction action, RequisitionStatus expectedTo)
    {
        Assert.True(RequisitionStateMachine.TryTransition(from, action, out var actualTo));
        Assert.Equal(expectedTo, actualTo);
    }

    public static TheoryData<RequisitionStatus, RequisitionAction, RequisitionStatus> AllowedCases()
    {
        var data = new TheoryData<RequisitionStatus, RequisitionAction, RequisitionStatus>();
        foreach (var (from, action, to) in Expected)
        {
            data.Add(from, action, to);
        }

        return data;
    }

    [Theory]
    [InlineData(RequisitionStatus.Draft, RequisitionAction.Approve)]
    [InlineData(RequisitionStatus.Draft, RequisitionAction.Issue)]
    [InlineData(RequisitionStatus.PendingApproval, RequisitionAction.Issue)]
    [InlineData(RequisitionStatus.Rejected, RequisitionAction.Approve)]
    [InlineData(RequisitionStatus.Rejected, RequisitionAction.Submit)]
    [InlineData(RequisitionStatus.Closed, RequisitionAction.Issue)]
    [InlineData(RequisitionStatus.Issued, RequisitionAction.Issue)]
    public void ForbiddenTransition_IsRejected(RequisitionStatus from, RequisitionAction action)
    {
        Assert.False(RequisitionStateMachine.TryTransition(from, action, out _));
    }
}
