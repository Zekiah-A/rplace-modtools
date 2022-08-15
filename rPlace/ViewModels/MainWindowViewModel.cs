﻿using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace rPlace.ViewModels;
public partial class MainWindowViewModel : ObservableObject
{
    public string[] KnownWebsockets => new[] {"wss://server2.rplace.tk", "wss://server.rplace.tk"};
    public string[] KnownFileServers => new[] {"https://server2.rplace.tk:8081/", "https://github.com/rslashplace2/rslashplace2.github.io/raw/main/"};
}