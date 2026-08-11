namespace SleipnirCore.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class SleipnirMethodAttribute : System.Attribute
    {
        private readonly string _name;

        public SleipnirMethodAttribute(string name)
        {
            _name = name;
        }

        public string Name => _name;
    }
}
