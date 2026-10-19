using System;
var host = Spike.HostInventory.FromCurrentProcess();
host.Dump(Console.Out);
Spike.InventoryExtras.Run(host);
