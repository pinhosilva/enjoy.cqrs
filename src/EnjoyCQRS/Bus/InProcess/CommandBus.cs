﻿using System.Threading.Tasks;
using EnjoyCQRS.Commands;
using EnjoyCQRS.Messages;

namespace EnjoyCQRS.Bus.InProcess
{
    public class CommandBus : InProcessBus, ICommandDispatcher
    {
        private readonly ICommandRouter _router;

        public CommandBus(ICommandRouter router)
        {
            _router = router;
        }
        
        public void Dispatch<TCommand>(TCommand command) where TCommand : ICommand
        {
            Send(command);
        }

        protected override Task RouteAsync(dynamic message)
        {
            return _router.RouteAsync(message);
        }
    }
}