try:
    from .app import main
except ImportError:
    from ytdlp_helper.app import main


if __name__ == "__main__":
    main()
