using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace CodexUsageMeter
{
    internal static class DarkTheme
    {
        internal static void Apply(FrameworkElement element)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUsageMeter.DarkTheme.xaml"))
                element.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }
    }
}
