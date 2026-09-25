using System.Configuration;

namespace Lithnet.Pan.RAProxy
{
    public class RAProxyConfigurationSection : ConfigurationSection
    {
        public const string SectionName = "ra-proxy-config";

        [ConfigurationProperty("debug-enabled", IsRequired = false, DefaultValue = false)]
        public bool DebuggingEnabled
        {
            get
            {
                return (bool)this["debug-enabled"];
            }
            set
            {
                this["debug-enabled"] = value;
            }
        }

        /// <summary>
        /// Timeout, in minutes, sent with every login entry. 0 omits it, so the firewall's global User-ID timeout applies.
        /// Keep it at least twice the NAS interim-update interval.
        /// </summary>
        [ConfigurationProperty("login-timeout-minutes", IsRequired = false, DefaultValue = 75)]
        [IntegerValidator(MinValue = 0, MaxValue = int.MaxValue)]
        public int LoginTimeoutMinutes
        {
            get
            {
                return (int)this["login-timeout-minutes"];
            }
            set
            {
                this["login-timeout-minutes"] = value;
            }
        }

        [ConfigurationProperty("username-filter", IsRequired = false, DefaultValue = null)]
        public string UsernameFilter
        {
            get
            {
                return (string)this["username-filter"];
            }
            set
            {
                this["username-filter"] = value;
            }
        }

        [ConfigurationProperty("username-rewrites")]
        public UsernameRewriteCollection UsernameRewrites
        {
            get
            {
                return (UsernameRewriteCollection)this["username-rewrites"];
            }

            set
            {
                this["username-rewrites"] = value;
            }
        }

        [ConfigurationProperty("radius-servers")]
        public RadiusServerCollection RadiusServers
        {
            get
            {
                return (RadiusServerCollection)this["radius-servers"];
            }

            set
            {
                this["radius-servers"] = value;
            }
        }

        [ConfigurationProperty("radius-client")]
        public RadiusClientSection RadiusClient
        {
            get
            {
                return (RadiusClientSection)this["radius-client"];
            }

            set
            {
                this["radius-client"] = value;
            }
        }

        [ConfigurationProperty("pan-api-endpoints")]
        public PanApiCollection PanApi
        {
            get
            {
                return (PanApiCollection)this["pan-api-endpoints"];
            }

            set
            {
                this["pan-api-endpoints"] = value;
            }
        }
    }
}