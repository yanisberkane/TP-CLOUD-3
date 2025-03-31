using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MVC.Data;
using MVC.Models;
using MVC.Services;
using SharedEvents.Events;
using System.Security.Claims;

namespace MVC.Controllers
{
    public class CommentsController : Controller
    {
        private readonly IRepository _repo;
        private readonly EventHubService _eventHub;

        public CommentsController(IRepository repo, EventHubService eventHub)
        {
            _repo = repo;
            _eventHub = eventHub;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("PostId,Commentaire,User,Created")] Comment comment)
        {
            if (ModelState.IsValid)
            {
                // Send CommentCreatedEvent to Event Hub
                var commentCreatedEvent = new CommentCreatedEvent
                {
                    CommentId = comment.Id,
                    PostId = comment.PostId,
                    Content = comment.Commentaire,
                    User = comment.User,
                    Created = comment.Created
                };

                await _eventHub.SendEventAsync(commentCreatedEvent);

                return RedirectToAction("Details", "Posts", new { id = comment.PostId });
            }
            return View(comment);
        }
    }
}