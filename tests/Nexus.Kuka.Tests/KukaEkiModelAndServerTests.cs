using Xunit;
using Nexus.Kuka;

namespace Nexus.Kuka.Tests
{
    public class KukaEkiModelTests
    {
        [Fact]
        public void Constants_CommonVariablesDefined()
        {
            Assert.NotEmpty(KukaEkiConstants.CommonVariables);
            Assert.Contains("$POS_ACT", KukaEkiConstants.CommonVariables);
            Assert.Contains("$AXIS_ACT", KukaEkiConstants.CommonVariables);
            Assert.Contains("$OV_PRO", KukaEkiConstants.CommonVariables);
        }

        [Fact]
        public void Models_EnumValues()
        {
            Assert.True(Enum.IsDefined(typeof(KukaControllerModel), KukaControllerModel.KrC4));
            Assert.True(Enum.IsDefined(typeof(KukaControllerModel), KukaControllerModel.KrC5));
        }

        [Fact]
        public void CoordinateSystem_Values()
        {
            Assert.Equal(0, (int)KukaCoordinateSystem.Base);
            Assert.Equal(1, (int)KukaCoordinateSystem.Tool);
            Assert.Equal(2, (int)KukaCoordinateSystem.World);
        }

        [Fact]
        public void RunMode_Values()
        {
            Assert.Equal(1, (int)KukaRunMode.T1);
            Assert.Equal(2, (int)KukaRunMode.T2);
            Assert.Equal(3, (int)KukaRunMode.Auto);
            Assert.Equal(4, (int)KukaRunMode.AutoExt);
        }
    }

    public class KukaEkiVirtualServerTests
    {
        [Fact]
        public void Server_StartsAndStops()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.Start();
            Assert.True(server.IsRunning);
            server.Stop();
        }

        [Fact]
        public void SetGetVariable()
        {
            using var server = new KukaEkiVirtualServer(0);
            server.SetVariable("MY_VAR", "42.5");
            Assert.Equal("42.5", server.GetVariable("MY_VAR"));
        }

        [Fact]
        public void GetUndefinedVariable_Null()
        {
            using var server = new KukaEkiVirtualServer(0);
            Assert.Null(server.GetVariable("NONEXISTENT"));
        }
    }
}
