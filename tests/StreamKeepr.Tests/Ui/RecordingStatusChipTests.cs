using StreamKeepr.Components.Primitives;
using StreamKeepr.Ui;

namespace StreamKeepr.Tests.Ui;

/// <summary>The one mapping from a recording's state to the chip the library and details show.</summary>
public sealed class RecordingStatusChipTests
{
    [Theory]
    [InlineData(RecordingState.Recording, StatusChipKind.Rec)]
    [InlineData(RecordingState.Reconnecting, StatusChipKind.Reconnecting)]
    [InlineData(RecordingState.Partial, StatusChipKind.Partial)]
    public void A_state_with_a_condition_to_show_maps_to_its_chip(RecordingState state, StatusChipKind chip) =>
        Assert.Equal(chip, RecordingStatusChip.For(state));

    [Fact]
    public void A_saved_recording_has_no_chip() =>
        Assert.Null(RecordingStatusChip.For(RecordingState.Saved));
}
