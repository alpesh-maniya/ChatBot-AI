namespace ChatBot.Api.Domain;

// An enum rather than a string so an invalid role can't reach the prompt builder.
public enum ChatRole
{
    System,
    User,
    Assistant
}
