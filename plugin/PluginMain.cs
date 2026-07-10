using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using AutoNAVMCP.Bridge;

namespace AutoNAVMCP
{
    /// <summary>
    /// AutoNAV MCP — Model Context Protocol bridge for Navisworks Manage.
    ///
    /// The Add-Ins ribbon button toggles a loopback TCP bridge that the
    /// companion MCP server (mcp-server/ in this repo) connects to, letting
    /// AI clients such as Claude identify, assign, resolve and report on
    /// clashes through the Navisworks .NET API.
    /// </summary>
    [Plugin("AutoNAVMCP",
        "ACLP_VDC",
        ToolTip = "AutoNAV MCP: AI clash coordination bridge (start/stop)",
        DisplayName = "AutoNAV MCP")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class PluginMain : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                if (!BridgeServer.IsRunning)
                {
                    MainThread.Initialize();
                    int port = BridgeServer.ResolvePort();
                    BridgeServer.Start(port);
                    MessageBox.Show(
                        "AutoNAV MCP bridge is running on 127.0.0.1:" + port + ".\n\n" +
                        "Connect an MCP client (e.g. Claude) through the AutoNAV MCP server. " +
                        "Click the AutoNAV MCP button again to stop the bridge.",
                        "AutoNAV MCP",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    DialogResult choice = MessageBox.Show(
                        "AutoNAV MCP bridge is running on 127.0.0.1:" + BridgeServer.Port + ".\n\n" +
                        "Stop the bridge?",
                        "AutoNAV MCP",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (choice == DialogResult.Yes)
                        BridgeServer.Stop();
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "AutoNAV MCP error:\n\n" + ex.Message,
                    "AutoNAV MCP",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
