using Avalonia.Controls;
using Docklys.ModuleContracts;

namespace Test
{
    public partial class Test : UserControl, IModule
    {
        // Identification
        public string Id => "Test";
        public string ModuleName => "Test";
        public string ModuleVersion => "1.0.0";
        public string Category => "Default";
        public string[] Tags => new [] { "Test", "example" };

        // Layout info — 1x1 = 110x110, matching VolumeMixer's footprint.
        public int TileWidth => 4;
        public int TileHeight => 5;

        // Compatibility
        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "2.0.0";
        public string[] SupportedPlatforms => new [] { "Windows", "Linux", "Mac" };
        
        // Unique Module ID (set by the main app)
        private string _uniqueModuleId;
        public string UniqueModuleId { get { return _uniqueModuleId; } }

        public void SetModuleId(string uniqueModuleId)
        {
            _uniqueModuleId = uniqueModuleId;
        }

        public void PrintModuleId()
        {
            Console.WriteLine($"Module ID: {UniqueModuleId}");
        }
        
        public Test()
        {
            InitializeComponent();
        }
    }
}