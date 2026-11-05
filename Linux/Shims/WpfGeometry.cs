// Stand-in so shared code compiles on Linux (S8): the two WPF geometry types the quick-switch wheel's layout
// (Views/QuickSwitchLayout.cs) names. Only what that code uses.
namespace System.Windows;

public readonly record struct Point(double X, double Y);

public readonly record struct Size(double Width, double Height);
