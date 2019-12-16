using Splat;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;

namespace OBSKeyboardMute
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        App()
        {
            Locator.CurrentMutable.InitializeReactiveUI();
        }
    }
}
