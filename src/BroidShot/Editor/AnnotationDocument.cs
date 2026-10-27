using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BroidShot.Editor;

public enum AnnotationTool { Region, Select, Arrow, Rectangle, Ellipse, Pen, Text, Number, Highlight, Redact }

public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnnotationTool Tool { get; set; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public Color Color { get; set; } = Colors.OrangeRed;
    public double Thickness { get; set; } = 4;
    public string Text { get; set; } = "";
    public List<Point> Points { get; set; } = new();
    public Rect Bounds
    {
        get
        {
            if (Tool == AnnotationTool.Number)
            {
                double radius = Math.Max(16, Thickness * 5);
                return new Rect(Start.X - radius, Start.Y - radius, radius * 2, radius * 2);
            }
            if (Tool == AnnotationTool.Pen && Points.Count > 0)
                return new Rect(new Point(Points.Min(p => p.X), Points.Min(p => p.Y)), new Point(Points.Max(p => p.X), Points.Max(p => p.Y)));
            return new Rect(Start, End);
        }
    }
    public Annotation Clone() => new() { Id = Id, Tool = Tool, Start = Start, End = End, Color = Color, Thickness = Thickness, Text = Text, Points = new(Points) };
    public void Move(Vector delta)
    {
        Start += delta;
        End += delta;
        for (int i = 0; i < Points.Count; i++) Points[i] += delta;
    }
    public bool Contains(Point point)
    {
        var bounds = Bounds;
        bounds.Inflate(Math.Max(8, Thickness * 2), Math.Max(8, Thickness * 2));
        return bounds.Contains(point);
    }
}

public sealed class AnnotationDocument
{
    private sealed record State(Rect Selection, List<Annotation> Items);
    private readonly Stack<State> _undo = new();
    private readonly Stack<State> _redo = new();
    public List<Annotation> Items { get; private set; } = new();
    public Rect Selection { get; set; }
    public Guid? SelectedId { get; set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Checkpoint()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
    }
    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Restore(_undo.Pop());
    }
    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Restore(_redo.Pop());
    }
    private State Snapshot() => new(Selection, Items.Select(x => x.Clone()).ToList());
    private void Restore(State state)
    {
        Selection = state.Selection;
        Items = state.Items.Select(x => x.Clone()).ToList();
        SelectedId = null;
    }
}
