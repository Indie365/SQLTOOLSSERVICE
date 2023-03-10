//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
#if false
using Microsoft.SqlTools.Extensions;
using Microsoft.SqlTools.Protocol.LanguageServer;
using Microsoft.SqlTools.Protocol.MessageProtocol;
using System.Threading.Tasks;

namespace Microsoft.SqlTools.Protocol.Server
{
    internal class LanguageServerEditorOperations : IEditorOperations
    {
        private EditorSession editorSession;
        private IMessageSender messageSender;

        public LanguageServerEditorOperations(
            EditorSession editorSession,
            IMessageSender messageSender)
        {
            this.editorSession = editorSession;
            this.messageSender = messageSender;
        }

        public async Task<EditorContext> GetEditorContext()
        {
            ClientEditorContext clientContext =
                await this.messageSender.SendRequest(
                    GetEditorContextRequest.Type,
                    new GetEditorContextRequest(),
                    true);

            return this.ConvertClientEditorContext(clientContext);
        }

        public async Task InsertText(string filePath, string text, BufferRange insertRange)
        {
            await this.messageSender.SendRequest(
                InsertTextRequest.Type,
                new InsertTextRequest
                {
                    FilePath = filePath,
                    InsertText = text,
                    InsertRange =
                        new Range
                        {
                            Start = new Position
                            {
                                Line = insertRange.Start.Line - 1,
                                Character = insertRange.Start.Column - 1
                            },
                            End = new Position
                            {
                                Line = insertRange.End.Line - 1,
                                Character = insertRange.End.Column - 1
                            }
                        }
                }, false);

            // TODO: Set the last param back to true!
        }

        public Task SetSelection(BufferRange selectionRange)
        {
            return this.messageSender.SendRequest(
                SetSelectionRequest.Type,
                new SetSelectionRequest
                {
                    SelectionRange =
                        new Range
                        {
                            Start = new Position
                            {
                                Line = selectionRange.Start.Line - 1,
                                Character = selectionRange.Start.Column - 1
                            },
                            End = new Position
                            {
                                Line = selectionRange.End.Line - 1,
                                Character = selectionRange.End.Column - 1
                            }
                        }
                }, true);
        }

        public EditorContext ConvertClientEditorContext(
            ClientEditorContext clientContext)
        {
            return
                new EditorContext(
                    this,
                    this.editorSession.Workspace.GetFile(clientContext.CurrentFilePath),
                    new BufferPosition(
                        clientContext.CursorPosition.Line + 1,
                        clientContext.CursorPosition.Character + 1),
                    new BufferRange(
                        clientContext.SelectionRange.Start.Line + 1,
                        clientContext.SelectionRange.Start.Character + 1,
                        clientContext.SelectionRange.End.Line + 1,
                        clientContext.SelectionRange.End.Character + 1));
        }

        public Task OpenFile(string filePath)
        {
            return
                this.messageSender.SendRequest(
                    OpenFileRequest.Type,
                    filePath,
                    true);
        }
    }
}
#endif