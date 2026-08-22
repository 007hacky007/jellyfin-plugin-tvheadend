using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TVHeadEnd.Configuration;
using System.IO;
using MediaBrowser.Model.Drawing;

namespace TVHeadEnd
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tvheadend",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.html",
                },
                new PluginPageInfo
                {
                    Name = "tvheadendjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.tvheadend.js"
                }
            };
        }

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return "TVHeadend Fail-Fast"; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "Provides live TV using TVHeadend as the source. Fork of the official plugin that stays responsive when the TVHeadend server is unreachable.";
            }
        }

        private Guid _id = new Guid("980dc09b-7127-4a6e-b101-b6ae374ea0cc");
        public override Guid Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }
    }

}
