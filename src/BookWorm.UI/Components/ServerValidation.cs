using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace BookWorm.UI.Components;

/// <summary>
/// Shows validation errors returned by the API next to the matching form fields. Errors for fields
/// the form doesn't have are collected in <see cref="GeneralErrors"/>.
/// </summary>
public sealed class ServerValidation : ComponentBase, IDisposable
{
    private ValidationMessageStore _store;

    [CascadingParameter]
    private EditContext EditContext { get; set; }

    public List<string> GeneralErrors { get; } = [];

    protected override void OnInitialized()
    {
        if (EditContext is null)
        {
            throw new InvalidOperationException($"{nameof(ServerValidation)} must be placed inside an EditForm.");
        }

        _store = new ValidationMessageStore(EditContext);
        EditContext.OnValidationRequested += OnValidationRequested;
        EditContext.OnFieldChanged += OnFieldChanged;
    }

    public void Show(IReadOnlyDictionary<string, string[]> errors)
    {
        if (EditContext is null || _store is null)
        {
            return;
        }

        Clear();
        var model = EditContext.Model;
        foreach (var (field, messages) in errors)
        {
            var property = model.GetType().GetProperties()
                .FirstOrDefault(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                GeneralErrors.AddRange(messages);
            }
            else
            {
                _store.Add(new FieldIdentifier(model, property.Name), messages);
            }
        }

        EditContext.NotifyValidationStateChanged();
    }

    public void Clear()
    {
        _store?.Clear();
        GeneralErrors.Clear();
        EditContext?.NotifyValidationStateChanged();
    }

    private void OnValidationRequested(object sender, ValidationRequestedEventArgs e) => Clear();

    private void OnFieldChanged(object sender, FieldChangedEventArgs e)
    {
        _store?.Clear(e.FieldIdentifier);
    }

    public void Dispose()
    {
        if (EditContext is not null)
        {
            EditContext.OnValidationRequested -= OnValidationRequested;
            EditContext.OnFieldChanged -= OnFieldChanged;
        }
    }
}
