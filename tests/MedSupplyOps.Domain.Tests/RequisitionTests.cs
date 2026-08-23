using MedSupplyOps.Domain.Requisitions;

namespace MedSupplyOps.Domain.Tests;

public sealed class RequisitionTests
{
    private static Requisition NewDraftWithOneLine()
    {
        var requisition = new Requisition(id: 1, departmentId: 10);
        requisition.AddLine(new RequisitionLine(itemId: 100, quantity: 5));
        return requisition;
    }

    [Fact]
    public void NewRequisition_StartsAsDraft()
    {
        Assert.Equal(RequisitionStatus.Draft, new Requisition(1, 10).Status);
    }

    [Fact]
    public void HappyPath_DraftToClosed()
    {
        var requisition = NewDraftWithOneLine();

        requisition.Submit();
        Assert.Equal(RequisitionStatus.PendingApproval, requisition.Status);

        requisition.Approve();
        Assert.Equal(RequisitionStatus.Approved, requisition.Status);

        requisition.Issue();
        Assert.Equal(RequisitionStatus.Issued, requisition.Status);

        requisition.Close();
        Assert.Equal(RequisitionStatus.Closed, requisition.Status);
    }

    [Fact]
    public void IllegalTransition_ThrowsInsteadOfBeingSilentlyIgnored()
    {
        // FR-304 明文要求：不合法的轉換必須被拒絕並回傳明確錯誤，不可靜默忽略。
        // 「靜默忽略」的症狀是：使用者按了核准、畫面沒報錯、單子卻還停在原狀態 ——
        // 沒有人會回報這種 bug，因為它看起來只是「我大概忘了按」。
        var requisition = NewDraftWithOneLine();

        var ex = Assert.Throws<InvalidOperationException>(requisition.Approve);
        Assert.Contains("Draft", ex.Message, StringComparison.Ordinal);

        // 狀態必須維持不變。
        Assert.Equal(RequisitionStatus.Draft, requisition.Status);
    }

    [Fact]
    public void SubmitWithNoLines_IsRejected()
    {
        var requisition = new Requisition(1, 10);

        Assert.Throws<InvalidOperationException>(requisition.Submit);
        Assert.Equal(RequisitionStatus.Draft, requisition.Status);
    }

    [Fact]
    public void EditingLines_IsOnlyAllowedInDraft()
    {
        var requisition = NewDraftWithOneLine();
        requisition.Submit();

        Assert.Throws<InvalidOperationException>(
            () => requisition.AddLine(new RequisitionLine(itemId: 200, quantity: 1)));
        Assert.Single(requisition.Lines);
    }

    [Fact]
    public void Reject_WithoutReason_FailsAndLeavesStatusUnchanged()
    {
        var requisition = NewDraftWithOneLine();
        requisition.Submit();

        Assert.Throws<ArgumentException>(() => requisition.Reject("   "));

        // 駁回失敗時，狀態不可以已經被改掉 —— 否則會出現「駁回原因是空的已駁回單」。
        Assert.Equal(RequisitionStatus.PendingApproval, requisition.Status);
        Assert.Null(requisition.RejectionReason);
    }

    [Fact]
    public void Reject_WithReason_RecordsTheReason()
    {
        var requisition = NewDraftWithOneLine();
        requisition.Submit();

        requisition.Reject("庫存不足，請改請領替代品項");

        Assert.Equal(RequisitionStatus.Rejected, requisition.Status);
        Assert.Equal("庫存不足，請改請領替代品項", requisition.RejectionReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RequisitionLine_RejectsNonPositiveQuantity(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequisitionLine(itemId: 1, quantity));
    }
}
