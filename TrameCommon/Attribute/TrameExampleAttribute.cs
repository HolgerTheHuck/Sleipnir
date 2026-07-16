using System;

namespace TrameCommon.Attribute
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class TrameExampleAttribute : System.Attribute
    {
        public string ExampleJson { get; }

        public TrameExampleAttribute(string exampleJson)
        {
            ExampleJson = exampleJson;
        }
    }
}
