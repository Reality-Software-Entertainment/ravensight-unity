// Ravensight campaign links in a WebGL build. Returns the page's own query
// string (window.location.search, which never includes the "#" part) so the
// SDK can read rs_link from it, once, when the Ravensight component wakes.
// It only reads: the page address is never changed and nothing is stored.
mergeInto(LibraryManager.library, {
  RavensightLandingSearch: function () {
    var search = '';
    try {
      if (typeof window !== 'undefined' && window.location && typeof window.location.search === 'string') {
        search = window.location.search;
      }
    } catch (e) {
      search = '';
    }
    // A returned string is handed back as UTF-8 in memory the runtime frees.
    var size = lengthBytesUTF8(search) + 1;
    var buffer = _malloc(size);
    stringToUTF8(search, buffer, size);
    return buffer;
  }
});
