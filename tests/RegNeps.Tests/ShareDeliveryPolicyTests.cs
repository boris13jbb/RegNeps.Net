using RegNeps.Application.Records;

namespace RegNeps.Tests;

/// <summary>
/// La selección de Captura no debe perderse por clipboard, cancelación o bridge nativo.
/// </summary>
public sealed class ShareDeliveryPolicyTests
{
    [Fact]
    public void Shared_Can_Clear_Selection()
    {
        Assert.True(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Shared, clearOnSharedSuccess: true));
    }

    [Fact]
    public void Shared_With_Preserve_Policy_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Shared, clearOnSharedSuccess: false));
    }

    [Fact]
    public void NativeRequested_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.NativeRequested, clearOnSharedSuccess: true));
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.NativeRequested, clearOnSharedSuccess: false));
    }

    [Fact]
    public void Copied_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Copied, clearOnSharedSuccess: true));
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Copied, clearOnSharedSuccess: false));
    }

    [Fact]
    public void Cancelled_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Cancelled, clearOnSharedSuccess: true));
    }

    [Fact]
    public void Unsupported_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Unsupported, clearOnSharedSuccess: true));
    }

    [Fact]
    public void Failed_Does_Not_Clear_Selection()
    {
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Failed, clearOnSharedSuccess: true));
    }

    [Theory]
    [InlineData("shared", ShareDeliveryResult.Shared)]
    [InlineData("native-requested", ShareDeliveryResult.NativeRequested)]
    [InlineData("copied", ShareDeliveryResult.Copied)]
    [InlineData("cancelled", ShareDeliveryResult.Cancelled)]
    [InlineData("unsupported", ShareDeliveryResult.Unsupported)]
    [InlineData("failed", ShareDeliveryResult.Failed)]
    [InlineData("SHARED", ShareDeliveryResult.Shared)]
    public void Parse_Maps_Known_Tokens(string token, ShareDeliveryResult expected)
    {
        Assert.Equal(expected, ShareDeliveryPolicy.Parse(token));
    }

    [Fact]
    public void Parse_Unknown_Is_Failed()
    {
        Assert.Equal(ShareDeliveryResult.Failed, ShareDeliveryPolicy.Parse("yes"));
        Assert.Equal(ShareDeliveryResult.Failed, ShareDeliveryPolicy.Parse(null));
        Assert.Equal(ShareDeliveryResult.Failed, ShareDeliveryPolicy.Parse(""));
    }

    [Fact]
    public void Messages_Do_Not_Mix_Shared_And_Copied()
    {
        var shared = ShareDeliveryPolicy.UserMessage(ShareDeliveryResult.Shared, 2);
        var copied = ShareDeliveryPolicy.UserMessage(ShareDeliveryResult.Copied, 2);
        var native = ShareDeliveryPolicy.UserMessage(ShareDeliveryResult.NativeRequested, 2);

        Assert.Contains("compart", shared!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("portapapeles", shared!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("portapapeles", copied!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compartidos", copied!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("menú", native!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("portapapeles", native!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Copied_Is_Not_Treated_As_Shared_Success_For_Bool_Compat()
    {
        // El contrato antiguo devolvía true también al copiar; eso ya no es "shared".
        Assert.NotEqual(ShareDeliveryResult.Shared, ShareDeliveryPolicy.Parse("copied"));
        Assert.False(ShareDeliveryPolicy.ShouldClearSelection(ShareDeliveryResult.Copied, clearOnSharedSuccess: true));
    }
}
