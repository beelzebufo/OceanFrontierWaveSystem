using System;
using Godot;

namespace OceanFrontier.Water.Debug;

internal static class OceanDiagnosticUi
{
	internal static VBoxContainer AddTab(
		TabContainer tabs,
		string title)
	{
		var scroll =
			new ScrollContainer
			{
				Name =
					title,

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,

				SizeFlagsVertical =
					Control.SizeFlags.ExpandFill,
			};


		var column =
			new VBoxContainer
			{
				CustomMinimumSize =
					new Vector2(
						360.0f,
						0.0f),

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		scroll.AddChild(
			column);


		tabs.AddChild(
			scroll);


		return column;
	}


	internal static VBoxContainer Foldout(
		VBoxContainer parent,
		string title,
		bool expanded = false)
	{
		var button =
			new Button
			{
				ToggleMode =
					true,

				ButtonPressed =
					expanded,

				Alignment =
					HorizontalAlignment.Left,

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		var content =
			new VBoxContainer
			{
				Visible =
					expanded,

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		void RefreshTitle(
			bool open)
		{
			button.Text =
				$"{(open ? "▼" : "▶")} {title}";
		}


		RefreshTitle(
			expanded);


		button.Toggled +=
			open =>
			{
				content.Visible =
					open;


				RefreshTitle(
					open);
			};


		parent.AddChild(
			button);


		parent.AddChild(
			content);


		return content;
	}


	internal static Label Header(
		VBoxContainer parent,
		string text)
	{
		var label =
			new Label
			{
				Text =
					text,

				HorizontalAlignment =
					HorizontalAlignment.Left,
			};


		parent.AddChild(
			label);


		parent.AddChild(
			new HSeparator());


		return label;
	}


	internal static Label Info(
		Node parent,
		string text)
	{
		var label =
			new Label
			{
				Text =
					text,

				AutowrapMode =
					TextServer.AutowrapMode.WordSmart,

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		parent.AddChild(
			label);


		return label;
	}


	internal static Button Button(
		Node parent,
		string text,
		Action action)
	{
		var button =
			new Button
			{
				Text =
					text,

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		button.Pressed +=
			action;


		parent.AddChild(
			button);


		return button;
	}


	internal static CheckBox Check(
		Node parent,
		string text,
		bool selected)
	{
		var box =
			new CheckBox
			{
				Text =
					text,

				ButtonPressed =
					selected,
			};


		parent.AddChild(
			box);


		return box;
	}


	internal static OptionButton Option(
		VBoxContainer parent,
		string label,
		params string[] items)
	{
		var row =
			new HBoxContainer
			{
				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		row.AddChild(
			new Label
			{
				Text =
					label,

				CustomMinimumSize =
					new Vector2(
						145.0f,
						0.0f),
			});


		var option =
			new OptionButton
			{
				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		foreach (string item in
				 items)
		{
			option.AddItem(
				item);
		}


		row.AddChild(
			option);


		parent.AddChild(
			row);


		return option;
	}


	internal static SpinBox Spin(
		VBoxContainer parent,
		string label,
		double initial,
		double min,
		double max,
		double step,
		string suffix = "")
	{
		var row =
			new HBoxContainer
			{
				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		row.AddChild(
			new Label
			{
				Text =
					label,

				CustomMinimumSize =
					new Vector2(
						145.0f,
						0.0f),
			});


		var spin =
			new SpinBox
			{
				MinValue =
					min,

				MaxValue =
					max,

				Step =
					step,

				Value =
					initial,

				Suffix =
					suffix,

				CustomMinimumSize =
					new Vector2(
						130.0f,
						0.0f),

				SizeFlagsHorizontal =
					Control.SizeFlags.ExpandFill,
			};


		row.AddChild(
			spin);


		parent.AddChild(
			row);


		return spin;
	}


	internal static void Clear(
		Node parent)
	{
		foreach (Node child in
				 parent.GetChildren())
		{
			parent.RemoveChild(
				child);


			child.QueueFree();
		}
	}
}
