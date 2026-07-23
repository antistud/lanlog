using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Logrr.Client.Logging
{
    /// <summary>Wires Logrr into the Microsoft.Extensions.Logging pipeline.</summary>
    public static class LogrrLoggingBuilderExtensions
    {
        /// <summary>
        /// Send logs to a Logrr server. Example:
        /// <c>builder.Logging.AddLogrr("https://logrr.internal", "lg_billing_...");</c>
        /// </summary>
        public static ILoggingBuilder AddLogrr(this ILoggingBuilder builder, string endpoint, string apiKey) =>
            builder.AddLogrr(options =>
            {
                options.Endpoint = endpoint;
                options.ApiKey = apiKey;
            });

        /// <summary>Send logs to a Logrr server, configuring all options.</summary>
        public static ILoggingBuilder AddLogrr(this ILoggingBuilder builder, Action<LogrrClientOptions> configure)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new LogrrClientOptions();
            configure(options);
            options.Validate();

            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ILoggerProvider>(new LogrrLoggerProvider(options)));
            return builder;
        }
    }
}
