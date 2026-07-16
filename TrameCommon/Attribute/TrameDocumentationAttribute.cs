using System;

namespace TrameCommon.Attribute
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
    public class TrameDocumentationAttribute : System.Attribute
    {
        public string Summary { get; }

        public TrameDocumentationAttribute(string summary)
        {
            Summary = summary;
        }
    }
}
