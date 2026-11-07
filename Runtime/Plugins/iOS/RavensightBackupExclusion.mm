// Copyright 2026 Reality Software Entertainment. MIT licensed.
//
// iOS only: marks a file or folder excluded from iCloud and device backups
// (NSURLIsExcludedFromBackupKey) and says whether it worked. Called from
// Ravensight.cs through DllImport("__Internal") for the persisted queue.

#import <Foundation/Foundation.h>

extern "C" bool RavensightExcludeFromBackup(const char* path)
{
    if (path == NULL) return false;
    @autoreleasepool
    {
        NSURL* url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        NSError* error = nil;
        return [url setResourceValue:@YES forKey:NSURLIsExcludedFromBackupKey error:&error] == YES;
    }
}
