using System;

namespace SleipnirCommon.Attribute
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
    public class SleipnirDocumentationAttribute : System.Attribute
    {
        public string Summary { get; }

        public SleipnirDocumentationAttribute(string summary)
        {
            Summary = summary;
        }
    }
}
