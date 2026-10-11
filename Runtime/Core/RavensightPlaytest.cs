using System;

namespace RealitySE
{
    /// <summary>
    /// A Ravensight Playtest run. Present only when the playtest runner
    /// launched this build; null for every real player.
    /// </summary>
    public sealed class RavensightPlaytestContext
    {
        public const string TokenVariable = "RAVENSIGHT_PLAYTEST_TOKEN";
        public const string RunIdVariable = "RAVENSIGHT_PLAYTEST_RUN_ID";
        public const string JobIdVariable = "RAVENSIGHT_PLAYTEST_JOB_ID";
        public const string PersonaVariable = "RAVENSIGHT_PLAYTEST_PERSONA";
        public const string TokenArgument = "--ravensight-playtest-token=";
        public const string RunIdArgument = "--ravensight-playtest-run-id=";

        public string Token;
        public string RunId;
        /// <summary>Only used to tag events.</summary>
        public string JobId;
        /// <summary>Only used to tag events.</summary>
        public string Persona;

        public RavensightPlaytestContext()
        {
        }

        public RavensightPlaytestContext(string token, string runId, string jobId, string persona)
        {
            Token = Clean(token);
            RunId = Clean(runId);
            JobId = Clean(jobId);
            Persona = Clean(persona);
        }

        public bool HasToken
        {
            get { return !string.IsNullOrEmpty(Clean(Token)); }
        }

        public bool HasRunId
        {
            get { return !string.IsNullOrEmpty(Clean(RunId)); }
        }

        /// <summary>
        /// Finds the playtest context, first source wins: an explicit context
        /// (config or Ravensight.SetPlaytestContext), then the environment
        /// variables, then the command line arguments. Returns null when no
        /// source carries a token. Each source supplies its own run id, so a
        /// token without one is reported as such rather than patched up.
        /// </summary>
        public static RavensightPlaytestContext Resolve(
            RavensightPlaytestContext explicitContext,
            Func<string, string> environment,
            string[] arguments)
        {
            if (explicitContext != null && explicitContext.HasToken)
            {
                return new RavensightPlaytestContext(
                    explicitContext.Token, explicitContext.RunId, explicitContext.JobId, explicitContext.Persona);
            }

            string jobId = Read(environment, JobIdVariable);
            string persona = Read(environment, PersonaVariable);

            string envToken = Read(environment, TokenVariable);
            if (!string.IsNullOrEmpty(envToken))
            {
                return new RavensightPlaytestContext(envToken, Read(environment, RunIdVariable), jobId, persona);
            }

            string argToken = Argument(arguments, TokenArgument);
            if (!string.IsNullOrEmpty(argToken))
            {
                return new RavensightPlaytestContext(argToken, Argument(arguments, RunIdArgument), jobId, persona);
            }

            return null;
        }

        private static string Read(Func<string, string> environment, string name)
        {
            if (environment == null) return null;
            try
            {
                return Clean(environment(name));
            }
            catch (Exception)
            {
                // Some platforms refuse environment access. That is not a playtest.
                return null;
            }
        }

        private static string Argument(string[] arguments, string prefix)
        {
            if (arguments == null) return null;
            for (int i = 0; i < arguments.Length; i++)
            {
                string arg = arguments[i];
                if (arg != null && arg.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return Clean(arg.Substring(prefix.Length));
                }
            }
            return null;
        }

        private static string Clean(string value)
        {
            if (value == null) return null;
            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
