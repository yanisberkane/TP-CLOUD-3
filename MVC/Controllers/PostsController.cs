using Microsoft.AspNetCore.Mvc;
using MVC.Models;
using MVC.Data;
using MVC.Business;
using Microsoft.AspNetCore.Authorization;
using SharedEvents.Events;
using MVC.Services; // ← Add this if EventHubService is inside MVC.Services
using System.Security.Claims;

namespace MVC.Controllers
{
    // [Authorize]
    public class PostsController : Controller
    {
        private readonly IRepository _repo;
        private readonly BlobController _blobController;
        private readonly ServiceBusController _serviceBusController;
        private readonly EventHubService _eventHub; // ← Injected EventHubService

        public PostsController(IRepository repo, BlobController blobController, ServiceBusController serviceBusController, EventHubService eventHub)
        {
            _repo = repo;
            _blobController = blobController;
            _serviceBusController = serviceBusController;
            _eventHub = eventHub;
        }

        // GET: Posts
        public async Task<IActionResult> Index(int pageNumber = 1, int pageSize = 10)
        {
            List<Post> posts = await _repo.GetPostsIndex(pageNumber, pageSize);
            int totalPosts = await _repo.GetPostsCount();
            int totalPages = (int)Math.Ceiling(totalPosts / (double)pageSize);

            PostIndexViewModel viewModel = new PostIndexViewModel { Posts = posts, CurrentPage = pageNumber, TotalPages = totalPages, PageSize = pageSize };

            return View(viewModel);
        }

        // GET: Posts/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: Posts/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Title,Category,User,Created,FileToUpload")] PostForm postForm)
        {
            try
            {
                postForm.BlobImage = Guid.NewGuid();
                postForm.Url = await _blobController.PushImageToBlob(postForm.FileToUpload, (Guid)postForm.BlobImage);

                ModelState.Remove("BlobImage");
                ModelState.Remove("Url");
            }
            catch (ExceptionFilesize)
            {
                ModelState.AddModelError("FileToUpload", "Le fichier est trop gros.");
            }

            if (ModelState.IsValid)
            {
                // Send original service bus messages
                await _serviceBusController.SendImageToResize((Guid)postForm.BlobImage!, postForm.Id);
                await _serviceBusController.SendContentImageToValidation((Guid)postForm.BlobImage!, Guid.NewGuid(), postForm.Id);

                // Send PostCreatedEvent to Event Hub
                var postCreatedEvent = new PostCreatedEvent
                {
                    PostId = postForm.Id,
                    Title = postForm.Title,
                    Category = postForm.Category.ToString(),
                    User = postForm.User,
                    Created = postForm.Created,
                    BlobImage = postForm.BlobImage!.Value,
                    Url = postForm.Url!
                };

                await _eventHub.SendEventAsync(postCreatedEvent);

                return RedirectToAction(nameof(Index));
            }

            return View(postForm);
        }

        [HttpPost]
        public async Task<ActionResult> Like(Guid id)
        {
            await _repo.IncrementPostLike(id);
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<ActionResult> Dislike(Guid id)
        {
            await _repo.IncrementPostDislike(id);
            return RedirectToAction("Index");
        }
    }
}
