using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class NotchStateManagerTests
{
    private readonly NotchStateManager _sm = new();

    #region Initial State

    [Fact]
    public void InitialState_IsCollapsed()
    {
        Assert.Equal(NotchState.Collapsed, _sm.CurrentState);
    }

    [Fact]
    public void InitialState_NotAnimating()
    {
        Assert.False(_sm.IsAnimating);
    }

    [Fact]
    public void InitialState_NotExpanded()
    {
        Assert.False(_sm.IsExpanded);
    }

    #endregion

    #region Valid Transitions

    [Fact]
    public void Collapsed_CanTransitionTo_Expanding()
    {
        Assert.True(_sm.CanTransitionTo(NotchState.Expanding));
        Assert.True(_sm.TryTransitionTo(NotchState.Expanding));
        Assert.Equal(NotchState.Expanding, _sm.CurrentState);
    }

    [Fact]
    public void Expanding_CanTransitionTo_Expanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        Assert.True(_sm.TryTransitionTo(NotchState.Expanded));
        Assert.Equal(NotchState.Expanded, _sm.CurrentState);
    }

    [Fact]
    public void Expanded_CanTransitionTo_Collapsing()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.True(_sm.TryTransitionTo(NotchState.Collapsing));
    }

    [Fact]
    public void Collapsing_CanTransitionTo_Collapsed()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        _sm.TryTransitionTo(NotchState.Collapsing);
        Assert.True(_sm.TryTransitionTo(NotchState.Collapsed));
        Assert.Equal(NotchState.Collapsed, _sm.CurrentState);
    }

    [Fact]
    public void Expanded_CanTransitionTo_SecondaryView()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.True(_sm.TryTransitionTo(NotchState.SecondaryView));
    }

    [Fact]
    public void Expanded_CannotTransitionTo_MusicExpanding()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.False(_sm.TryTransitionTo(NotchState.MusicExpanding));
    }

    [Fact]
    public void Collapsed_CanTransitionTo_MusicExpanding()
    {
        Assert.True(_sm.TryTransitionTo(NotchState.MusicExpanding));
    }

    [Fact]
    public void MusicExpanding_CanTransitionTo_MusicExpanded()
    {
        _sm.TryTransitionTo(NotchState.MusicExpanding);
        Assert.True(_sm.TryTransitionTo(NotchState.MusicExpanded));
    }

    [Fact]
    public void MusicExpanded_CanTransitionTo_MusicCollapsing()
    {
        _sm.TryTransitionTo(NotchState.MusicExpanding);
        _sm.TryTransitionTo(NotchState.MusicExpanded);
        Assert.True(_sm.TryTransitionTo(NotchState.MusicCollapsing));
    }

    #endregion

    #region Invalid Transitions

    [Fact]
    public void Collapsed_CannotTransitionTo_Expanded_Directly()
    {
        Assert.False(_sm.CanTransitionTo(NotchState.Expanded));
        Assert.False(_sm.TryTransitionTo(NotchState.Expanded));
        Assert.Equal(NotchState.Collapsed, _sm.CurrentState);
    }

    [Fact]
    public void Collapsed_CannotTransitionTo_Collapsing()
    {
        Assert.False(_sm.TryTransitionTo(NotchState.Collapsing));
    }

    [Fact]
    public void Expanding_CannotTransitionTo_MusicExpanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        Assert.False(_sm.TryTransitionTo(NotchState.MusicExpanded));
    }

    #endregion

    #region Derived Properties

    [Fact]
    public void IsTransitioning_TrueWhenExpanding()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        Assert.True(_sm.IsTransitioning);
    }

    [Fact]
    public void IsTransitioning_TrueWhenCollapsing()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        _sm.TryTransitionTo(NotchState.Collapsing);
        Assert.True(_sm.IsTransitioning);
    }

    [Fact]
    public void IsTransitioning_FalseWhenExpanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.False(_sm.IsTransitioning);
    }

    [Fact]
    public void IsExpanded_TrueForExpanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.True(_sm.IsExpanded);
    }

    [Fact]
    public void IsExpanded_TrueForSecondaryView()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        _sm.TryTransitionTo(NotchState.SecondaryView);
        Assert.True(_sm.IsExpanded);
    }

    [Fact]
    public void IsMusicExpanded_TrueOnlyForMusicExpanded()
    {
        _sm.TryTransitionTo(NotchState.MusicExpanding);
        Assert.False(_sm.IsMusicExpanded);
        _sm.TryTransitionTo(NotchState.MusicExpanded);
        Assert.True(_sm.IsMusicExpanded);
    }

    [Fact]
    public void IsSecondaryView_TrueOnlyForSecondaryView()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.False(_sm.IsSecondaryView);
        _sm.TryTransitionTo(NotchState.SecondaryView);
        Assert.True(_sm.IsSecondaryView);
    }

    #endregion

    #region Hidden State

    [Fact]
    public void Hidden_CanBeEnteredFromAnyState()
    {
        Assert.True(_sm.CanTransitionTo(NotchState.Hidden));

        _sm.TryTransitionTo(NotchState.Expanding);
        Assert.True(_sm.CanTransitionTo(NotchState.Hidden));
    }

    [Fact]
    public void Hidden_ReturnsToCollapsed_ViaShow()
    {
        _sm.ForceState(NotchState.Hidden);
        _sm.Show();
        Assert.Equal(NotchState.Collapsed, _sm.CurrentState);
    }

    #endregion

    #region ForceState

    [Fact]
    public void ForceState_BypassesTransitionTable()
    {
        _sm.ForceState(NotchState.MusicExpanded);
        Assert.Equal(NotchState.MusicExpanded, _sm.CurrentState);
    }

    #endregion

    #region StateChanged Event

    [Fact]
    public void StateChanged_FiresOnTransition()
    {
        NotchState? from = null, to = null;
        _sm.StateChanged += (s, e) => { from = e.PreviousState; to = e.NewState; };

        _sm.TryTransitionTo(NotchState.Expanding);

        Assert.Equal(NotchState.Collapsed, from);
        Assert.Equal(NotchState.Expanding, to);
    }

    [Fact]
    public void StateChanged_DoesNotFireOnInvalidTransition()
    {
        bool fired = false;
        _sm.StateChanged += (s, e) => fired = true;

        _sm.TryTransitionTo(NotchState.Expanded);

        Assert.False(fired);
    }

    #endregion

    #region Legacy Compatibility

    [Fact]
    public void CanExpand_TrueWhenCollapsed()
    {
        Assert.True(_sm.CanExpand());
    }

    [Fact]
    public void CanExpand_FalseWhenExpanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.False(_sm.CanExpand());
    }

    [Fact]
    public void CanCollapse_TrueWhenExpanded()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.True(_sm.CanCollapse());
    }

    #endregion

    #region GetCollapseTarget

    [Fact]
    public void GetCollapseTarget_FromExpanded_ReturnsCollapsing()
    {
        _sm.TryTransitionTo(NotchState.Expanding);
        _sm.TryTransitionTo(NotchState.Expanded);
        Assert.Equal(NotchState.Collapsing, _sm.GetCollapseTarget());
    }

    [Fact]
    public void GetCollapseTarget_FromMusicExpanded_ReturnsMusicCollapsing()
    {
        _sm.TryTransitionTo(NotchState.MusicExpanding);
        _sm.TryTransitionTo(NotchState.MusicExpanded);
        Assert.Equal(NotchState.MusicCollapsing, _sm.GetCollapseTarget());
    }

    [Fact]
    public void GetCollapseTarget_FromCollapsed_ReturnsCollapsed()
    {
        Assert.Equal(NotchState.Collapsed, _sm.GetCollapseTarget());
    }

    #endregion
}
