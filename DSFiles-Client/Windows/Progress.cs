using System;
using System.Threading;

namespace DSFiles_Client.CGuis
{
    public partial class Progress
    {
        

        public Progress(Action action)
        {
            InitializeComponent();
            Thread staThread = new Thread(() => action());
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            //Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
    }
}