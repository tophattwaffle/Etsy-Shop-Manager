using EtsyShopManager.Web.Components.Shared;

namespace EtsyShopManager.Tests;

/// <summary><see cref="SavedValues{T}"/>: how forms know they have unsaved changes and what Cancel puts back.</summary>
public class UnsavedChangesTests
{
    private sealed record Fields(string Note, bool Star);

    private sealed class Form
    {
        public string? Note = "Thanks!";
        public bool Star;
        public SavedValues<Fields> Saved = null!;

        public Form() => Saved = new(() => new(Note ?? "", Star), v => (Note, Star) = (v.Note, v.Star));
    }

    [Fact]
    public void A_form_starts_without_changes()
    {
        Assert.False(new Form().Saved.Changed);
    }

    [Fact]
    public void An_edit_is_a_change_until_it_is_typed_back()
    {
        var form = new Form { Note = "Thanks for your order" };
        form.Saved.Accept();

        form.Note = "Thanks for your order!";
        Assert.True(form.Saved.Changed);

        form.Note = "Thanks for your order";
        Assert.False(form.Saved.Changed);

        form.Star = true;
        Assert.True(form.Saved.Changed);
    }

    [Fact]
    public void Cancel_puts_the_last_saved_values_back()
    {
        var form = new Form();
        form.Note = "Saved note";
        form.Saved.Accept();

        form.Note = "Half typed";
        form.Star = true;
        form.Saved.Revert();

        Assert.Equal("Saved note", form.Note);
        Assert.False(form.Star);
        Assert.False(form.Saved.Changed);
    }

    [Fact]
    public void Saving_makes_the_current_values_the_saved_ones()
    {
        var form = new Form();
        form.Note = "New note";
        form.Saved.Accept();
        form.Note = "Newer";
        form.Saved.Revert();

        Assert.Equal("New note", form.Note);
    }

    [Fact]
    public void Clearing_a_field_that_had_no_value_is_not_a_change()
    {
        var form = new Form { Note = null };
        form.Saved.Accept();
        form.Note = "";

        Assert.False(form.Saved.Changed);
    }
}
