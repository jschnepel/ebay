using Auction.Domain;
using static Auction.UnitTests.TestAuctions;

namespace Auction.UnitTests;

/// <summary>Lifecycle transitions and outcomes, frozen terms, listing validation and cancellation.</summary>
public sealed class LifecycleTests
{
    private static AuctionState Draft(AuctionTerms terms) => AuctionState.NewDraft(Guid.NewGuid(), Seller, terms);

    [Fact]
    public void Publishing_before_the_start_schedules_and_after_the_start_activates_now()
    {
        var draft = Draft(Terms());

        var early = Lifecycle.Publish(draft, Seller, Start.AddMinutes(-10), AuctionLimits.Default);
        var late = Lifecycle.Publish(draft, Seller, During, AuctionLimits.Default);

        Assert.Equal(AuctionStatus.Scheduled, early.State.Status);
        Assert.Equal(AuctionStatus.Active, late.State.Status);
        Assert.Equal(During, late.State.Terms.StartsAt); // a past start means "start now"
    }

    [Fact]
    public void Only_the_owner_can_publish_or_edit_a_draft()
    {
        var draft = Draft(Terms());

        Assert.Equal(LifecycleError.NotOwner, Lifecycle.Publish(draft, A, During, AuctionLimits.Default).Error);
        Assert.Equal(LifecycleError.NotOwner, Lifecycle.EditTerms(draft, A, Terms(opening: 5m)).Error);
        Assert.True(Lifecycle.EditTerms(draft, Seller, Terms(opening: 5m)).Succeeded); // control
    }

    [Fact]
    public void Published_terms_are_frozen()
    {
        var published = Lifecycle.Publish(Draft(Terms()), Seller, During, AuctionLimits.Default).State;

        var edit = Lifecycle.EditTerms(published, Seller, Terms(opening: 5m));

        Assert.Equal(LifecycleError.InvalidState, edit.Error);
        Assert.Same(published, edit.State);
    }

    [Fact]
    public void Publishing_an_invalid_draft_names_each_failing_field()
    {
        var bad = Terms(floor: FloorType.Reserve, opening: 50m, reserve: 40m) with
        {
            Title = "ab",
            StartsAt = End,
            EndsAt = Start,
        };

        var result = Lifecycle.Publish(Draft(bad), Seller, Start.AddDays(-1), AuctionLimits.Default);

        Assert.Equal(LifecycleError.ValidationFailed, result.Error);
        Assert.Equal(AuctionStatus.Draft, result.State.Status);
        Assert.Contains(new FieldError("title", "too_short"), result.FieldErrors);
        Assert.Contains(new FieldError("reserve", "below_opening_price"), result.FieldErrors);
        Assert.Contains(new FieldError("endsAt", "not_after_start"), result.FieldErrors);
    }

    [Fact]
    public void Publishing_with_an_end_in_the_past_is_refused()
    {
        var result = Lifecycle.Publish(Draft(Terms()), Seller, End.AddMinutes(1), AuctionLimits.Default);

        Assert.Contains(new FieldError("endsAt", "in_past"), result.FieldErrors);
    }

    [Theory]
    [InlineData(0.5, "too_short")]
    [InlineData(14 * 24 * 60 + 1, "too_long")]
    public void Duration_must_be_between_one_minute_and_fourteen_days(double minutes, string code)
    {
        var terms = Terms() with { EndsAt = Start.AddMinutes(minutes) };

        var result = Lifecycle.Publish(Draft(terms), Seller, Start.AddMinutes(-1), AuctionLimits.Default);

        Assert.Contains(new FieldError("duration", code), result.FieldErrors);
    }

    [Fact]
    public void Reserve_is_required_for_the_reserve_floor_and_refused_for_the_others()
    {
        var missing = Terms(floor: FloorType.Reserve);
        var unexpected = Terms(floor: FloorType.Absolute) with { Reserve = Usd(100m) };

        Assert.Contains(new FieldError("reserve", "required"),
            ListingValidator.ValidateForPublish(missing, Start.AddMinutes(-1), AuctionLimits.Default));
        Assert.Contains(new FieldError("reserve", "not_allowed"),
            ListingValidator.ValidateForPublish(unexpected, Start.AddMinutes(-1), AuctionLimits.Default));
        Assert.Empty(ListingValidator.ValidateForPublish(Terms(), Start.AddMinutes(-1), AuctionLimits.Default)); // control
    }

    [Fact]
    public void Scheduled_becomes_active_at_its_start_and_not_before()
    {
        var scheduled = Draft(Terms()) with { Status = AuctionStatus.Scheduled };

        Assert.Equal(LifecycleError.NotDue, Lifecycle.Activate(scheduled, Start.AddTicks(-1)).Error);
        Assert.Equal(AuctionStatus.Active, Lifecycle.Activate(scheduled, Start).State.Status);
    }

    [Theory]
    [InlineData(FloorType.MinimumBid)]
    [InlineData(FloorType.Absolute)]
    public void Closing_with_bids_sells_to_the_leader_at_the_current_price_not_their_maximum(FloorType floor)
    {
        var state = WithBids(Active(Terms(floor: floor, opening: 10m)), (A, 50m), (B, 30m));

        var closed = Lifecycle.Close(state, End).State;

        Assert.Equal(AuctionStatus.Closed, closed.Status);
        Assert.Equal(CloseOutcome.Sold, closed.Outcome);
        Assert.Equal(A, closed.LeaderId);
        Assert.Equal(Usd(31m), closed.CurrentPrice);
    }

    [Fact]
    public void Spec_example_reserve_auction_closing_below_the_reserve_is_reserve_not_met()
    {
        var terms = Terms(BidEntry.FixedIncrement, FloorType.Reserve, opening: 70m, increment: 10m, reserve: 100m);

        var at80 = WithBids(Active(terms), (A, 70m), (B, 80m));
        var at100 = WithBids(at80, (A, 90m), (B, 100m));

        Assert.Equal(CloseOutcome.ReserveNotMet, Lifecycle.Close(at80, End).State.Outcome);
        Assert.Equal(CloseOutcome.Sold, Lifecycle.Close(at100, End).State.Outcome); // control
    }

    [Fact]
    public void Closing_without_bids_is_no_bids()
    {
        Assert.Equal(CloseOutcome.NoBids, Lifecycle.Close(Active(Terms()), End).State.Outcome);
    }

    [Fact]
    public void A_suspended_auction_closes_by_the_same_rules_and_a_scheduled_one_past_its_end_closes_too()
    {
        var suspended = Lifecycle.Suspend(WithBids(Active(Terms()), (A, 50m))).State;
        var scheduled = Draft(Terms()) with { Status = AuctionStatus.Scheduled };

        Assert.Equal(CloseOutcome.Sold, Lifecycle.Close(suspended, End).State.Outcome);
        Assert.Equal(CloseOutcome.NoBids, Lifecycle.Close(scheduled, End).State.Outcome);
    }

    [Fact]
    public void Closing_before_the_end_or_twice_changes_nothing()
    {
        var state = WithBids(Active(Terms()), (A, 50m));
        var closed = Lifecycle.Close(state, End).State;

        Assert.Equal(LifecycleError.NotDue, Lifecycle.Close(state, End.AddTicks(-1)).Error);
        var again = Lifecycle.Close(closed, End.AddHours(1));
        Assert.Equal(LifecycleError.InvalidState, again.Error);
        Assert.Same(closed, again.State);
    }

    [Fact]
    public void Owner_can_cancel_before_any_accepted_bid_but_not_after()
    {
        var fresh = Active(Terms());
        var withBid = WithBids(fresh, (A, 10m));

        Assert.Equal(AuctionStatus.Cancelled, Lifecycle.CancelByOwner(fresh, Seller).State.Status); // control
        Assert.Equal(LifecycleError.HasBids, Lifecycle.CancelByOwner(withBid, Seller).Error);
        Assert.Equal(LifecycleError.NotOwner, Lifecycle.CancelByOwner(fresh, A).Error);
    }

    [Theory]
    [InlineData(AuctionStatus.Suspended)]
    [InlineData(AuctionStatus.Closed)]
    [InlineData(AuctionStatus.Cancelled)]
    public void Owner_cannot_cancel_a_suspended_closed_or_cancelled_auction(AuctionStatus status)
    {
        var state = Active(Terms()) with { Status = status };

        Assert.Equal(LifecycleError.InvalidState, Lifecycle.CancelByOwner(state, Seller).Error);
    }

    [Fact]
    public void Resume_returns_to_active_only_before_the_end()
    {
        var suspended = Lifecycle.Suspend(Active(Terms())).State;

        Assert.Equal(AuctionStatus.Active, Lifecycle.Resume(suspended, During).State.Status);
        Assert.Equal(LifecycleError.Ended, Lifecycle.Resume(suspended, End).Error);
        Assert.Equal(LifecycleError.InvalidState, Lifecycle.Suspend(suspended).Error);
    }
}
