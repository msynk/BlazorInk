using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Rendering;

namespace BlazorInk.Tools;

/// <summary>Raised when the text tool wants the host to show a text editor.</summary>
/// <param name="Element">The text element being edited (already in the document).</param>
/// <param name="ViewRect">Where to place the editor, in view (CSS pixel) coordinates.</param>
public readonly record struct TextEditRequest(TextElement Element, Rect ViewRect);

/// <summary>
/// Places and edits text annotations. The tool creates the element and asks the host to show a real HTML text
/// input over it, because an accessible, IME-capable text editor cannot be built on a canvas.
/// </summary>
public sealed class TextTool : ToolBase
{
    /// <inheritdoc />
    public override string Id => "text";

    /// <inheritdoc />
    public override string DisplayName => "Text";

    /// <inheritdoc />
    public override string Cursor => "text";

    /// <summary>Text style for new text elements.</summary>
    public TextStyle Style { get; set; } = TextStyle.Default;

    /// <summary>Default width of a new text box in document units, used to size the editor.</summary>
    public float DefaultWidth { get; set; } = 200f;

    /// <summary>The element currently being edited, if any.</summary>
    public TextElement? Editing { get; private set; }

    private IDisposable? _creationGroup;
    private Guid? _createdElementId;

    /// <summary>Raised when the host should show or move a text editor.</summary>
    public event Action<TextEditRequest>? EditRequested;

    /// <summary>Raised when editing finished and the editor should be hidden.</summary>
    public event Action? EditFinished;

    /// <inheritdoc />
    public override bool OnPointerDown(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        Vector2 doc = context.Viewport.ToDocument(sample.Position);

        // Editing an existing text element takes priority over creating a new one.
        InkElement? hit = DocumentQuery.HitTest(context.Document, doc, 4f / context.Viewport.Zoom);
        if (hit is TextElement existing)
        {
            BeginEdit(context, existing);
            return true;
        }

        if (context.Document.TryGetLayer(context.ActiveLayerId, out Layer? layer) && (layer.IsLocked || !layer.IsVisible)) return false;

        var element = new TextElement(doc, string.Empty)
        {
            Style = Style with { Color = context.ShapeStyle.StrokeColor },
            Size = new Vector2(DefaultWidth, Style.FontSize * Style.LineHeight),
        };
        // Placing the box and typing into it is one action to the user, so it is one undo step. The group opens
        // before the element is added and stays open until the edit is committed or cancelled.
        _creationGroup = context.History.BeginGroup("Add text");
        try
        {
            context.Document.AddElement(element, context.ActiveLayerId);
        }
        catch (InvalidOperationException ex)
        {
            context.Announce("Text could not be added: " + ex.Message);
            EndCreationGroup();
            return false;
        }

        _createdElementId = element.Id;
        BeginEdit(context, element);
        return true;
    }

    /// <summary>Starts editing an element and asks the host for an editor.</summary>
    public void BeginEdit(IToolContext context, TextElement element)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(element);
        Editing = element;
        EditRequested?.Invoke(new TextEditRequest(element, context.Viewport.ToView(element.Bounds)));
        context.InvalidateOverlay();
    }

    /// <summary>
    /// Applies edited text. An empty result removes the element, so an abandoned text box does not leave an
    /// invisible object behind.
    /// </summary>
    /// <param name="context">Tool context.</param>
    /// <param name="text">The new text.</param>
    /// <param name="measuredSize">Size measured by the host in document units, or null to estimate.</param>
    public void CommitEdit(IToolContext context, string text, Vector2? measuredSize = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        TextElement? element = Editing;
        Editing = null;
        EditFinished?.Invoke();
        if (element is null)
        {
            EndCreationGroup();
            return;
        }
        if (!context.Document.ContainsElement(element.Id))
        {
            EndCreationGroup();
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                context.Document.RemoveElement(element.Id);
                context.InvalidateDocument(element.Bounds);
                return;
            }
            var updated = element with { Text = text, Size = measuredSize };
            context.Document.ReplaceElement(updated);
            context.InvalidateDocument(updated.Bounds.Union(element.Bounds));
            context.Announce("Text updated.");
        }
        catch (InvalidOperationException ex)
        {
            context.Announce("The text could not be saved: " + ex.Message);
        }
        finally
        {
            EndCreationGroup();
        }
    }

    /// <summary>Cancels editing, removing the element when it was newly created and is still empty.</summary>
    public void CancelEdit(IToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TextElement? element = Editing;
        Editing = null;
        EditFinished?.Invoke();
        if (element is not null && element.Text.Length == 0 && context.Document.ContainsElement(element.Id))
        {
            context.Document.RemoveElement(element.Id);
            // The box was created and removed within this gesture, so it never happened as far as undo cares.
            if (_createdElementId == element.Id) context.History.CancelGroup();
        }
        EndCreationGroup();
        context.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void Deactivate(IToolContext context)
    {
        CancelEdit(context);
        EndCreationGroup();
    }

    /// <summary>Closes the undo group that spans placing a text box and typing into it.</summary>
    private void EndCreationGroup()
    {
        _creationGroup?.Dispose();
        _creationGroup = null;
        _createdElementId = null;
    }

    /// <inheritdoc />
    public override void DrawOverlay(IToolContext context, IRenderContext render)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(render);
        if (Editing is null) return;
        // The HTML editor covers the element; draw only a focus outline around it.
        float zoom = MathF.Max(context.Viewport.Zoom, 0.001f);
        render.StrokePath(new PathBuilder(6).AddRect(Editing.Bounds).ToGeometry(),
            new StrokePaint(new InkColor(37, 99, 235), 1f / zoom, 1f, BlendMode.Normal, LineCap.Butt, LineJoin.Miter, [3f / zoom, 3f / zoom]));
    }

    /// <inheritdoc />
    public override bool OnKeyDown(IToolContext context, ToolKeyEvent key)
    {
        if (key.Key == "Escape" && Editing is not null)
        {
            CancelEdit(context);
            return true;
        }
        return false;
    }
}
