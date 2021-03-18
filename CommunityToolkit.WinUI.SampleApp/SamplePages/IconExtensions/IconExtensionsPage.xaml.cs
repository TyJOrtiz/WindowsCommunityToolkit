// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;

namespace CommunityToolkit.WinUI.SampleApp.SamplePages
{
    /// <summary>
    /// A page that shows how to use the offset behavior.
    /// </summary>
    public sealed partial class IconExtensionsPage : IXamlRenderListener
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IconExtensionsPage"/> class.
        /// </summary>
        public IconExtensionsPage()
        {
            InitializeComponent();
        }

        public void OnXamlRendered(FrameworkElement control)
        {
        }
    }
}